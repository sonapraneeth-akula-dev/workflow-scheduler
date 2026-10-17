using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Wf.Contracts;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Definition;
using Wf.Contracts.Events;
using Wf.Contracts.Runs;
using Wf.ControlPlane.Data;
using Wf.ControlPlane.Engine.Agents;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Leadership;
using Wf.ControlPlane.Engine.Recovery;
using Wf.ControlPlane.Engine.Reservations;
using Wf.ControlPlane.Registry;
using Wf.ControlPlane.Storage;
using Wf.Engine.Core;
using Wf.Engine.Core.Placement;
using Wf.Engine.Core.StateMachine;
using Wf.Integration.Tests.Agents;
using Wf.Sdk;
using DispatchCommand = Wf.Contracts.Agent.V1.Dispatch;

namespace Wf.Integration.Tests.Execution;

public sealed partial class HostedRunExecutionTests(HostedExecutionDatabase database) : IClassFixture<HostedExecutionDatabase>
{
    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Authored_resource_caps_reach_durable_dispatch_and_RAM_placement_before_execution()
    {
        await using var runtime = await Runtime.Create(database, w => w
            .Node("work", n => n.Task("task", _ => { }, p => p.Limits(cpuCores: 0.25, ramMb: 192))));
        await runtime.Tick();
        var command = Assert.Single(await runtime.Pending());
        Assert.Equal(0.25, command.Limits.CpuCores);
        Assert.Equal(192UL, command.Limits.RamMb);
        await runtime.FreshTick();
        Assert.Equal(command, Assert.Single(await runtime.Pending()));
        await runtime.Complete(command, TaskOutcome.Failed, "ResourceLimit", true);
        await runtime.Finish();
        Assert.Equal(ExecutionStatus.Failed, (await runtime.Load()).Snapshot.Tasks["work/task"]);
        Assert.Equal(1L, await runtime.Scalar("SELECT count(*) FROM task_attempts WHERE run_id=$1 AND task_key='work/task'"));
    }

    [Fact]
    public async Task RAM_cap_too_large_for_machine_waits_without_sending_an_unsafe_dispatch()
    {
        await using var runtime = await Runtime.Create(database, w => w
            .Node("work", n => n.Task("task", _ => { }, p => p.Limits(ramMb: 32768))));
        await runtime.Tick();
        Assert.Empty(await runtime.Pending());
        Assert.Equal("WaitingForMachine", (await runtime.Load()).Snapshot.TaskWaitReasons["work/task"]);
    }

    [Theory]
    [InlineData("TaskError", false, true, 1, 0)]
    [InlineData("PlatformError", false, true, 0, 1)]
    [InlineData("TaskError", true, false, 0, 0)]
    [InlineData("ValidationError", false, false, 0, 0)]
    [InlineData("ResourceLimit", false, false, 0, 0)]
    public async Task Agent_completion_commits_retry_before_terminal_state_and_preserves_exact_budget(
        string category, bool nonRetryable, bool retry, short author, short platform)
    {
        await using var runtime = await Runtime.Create(database, w => w
            .Node("work", n => n.Task("task", _ => { }, p => p.Retry(1, Backoff.Fixed(TimeSpan.FromSeconds(1)))))
            .PostProcess(_ => { }));
        await runtime.Tick();
        var command = Assert.Single(await runtime.Pending());
        await runtime.Complete(command, TaskOutcome.Failed, category, nonRetryable);
        var after = await runtime.Load();
        Assert.Equal(retry ? ExecutionStatus.Retrying : ExecutionStatus.Failed, after.Snapshot.Tasks["work/task"]);
        var sequence = after.Snapshot.LastSequence;
        await runtime.Complete(command, TaskOutcome.Failed, category, nonRetryable);
        Assert.Equal(sequence, (await runtime.Load()).Snapshot.LastSequence);
        Assert.Equal((short)ExecutionStatus.Failed,
            await runtime.Scalar("SELECT status FROM task_attempts WHERE run_id=$1 AND task_key='work/task' AND attempt=1"));
        Assert.NotNull(await runtime.Scalar("SELECT ended_at FROM task_attempts WHERE run_id=$1 AND task_key='work/task' AND attempt=1"));
        if (retry)
        {
            runtime.Clock.Now = runtime.Clock.Now.AddSeconds(2);
            await runtime.Tick();
            var next = Assert.Single(await runtime.Pending());
            Assert.Equal(2u, next.Task.Attempt);
            Assert.Equal(author, await runtime.Scalar("SELECT author_retries FROM task_attempts WHERE run_id=$1 AND task_key='work/task' AND attempt=2"));
            Assert.Equal(platform, await runtime.Scalar("SELECT platform_retries FROM task_attempts WHERE run_id=$1 AND task_key='work/task' AND attempt=2"));
            await runtime.Complete(next);
        }
        await runtime.Finish();
        Assert.Equal(retry ? RunStatus.Succeeded : RunStatus.Failed, (await runtime.Load()).Snapshot.Status);
        Assert.Equal(ExecutionStatus.Succeeded, (await runtime.Load()).Snapshot.Nodes["$post"]);
    }

    [Fact]
    public async Task Missing_declared_output_is_delivered_to_the_task_without_crashing_the_hosted_planner()
    {
        await using var runtime = await Runtime.Create(database, w => w
            .Node("source", n => n.Task("produce", _ => { }))
            .Node("consumer", n => n.DependsOn("source").Input("source", "missing")
                .Task("read", context => context.Input<string>("source", "missing"))));
        await runtime.Tick();
        await runtime.Complete(Assert.Single(await runtime.Pending()));
        await runtime.Tick();
        var consumer = Assert.Single(await runtime.Pending());
        Assert.Equal("consumer", consumer.Task.NodeKey);
        using var inputs = JsonDocument.Parse(consumer.InputsJson.Memory);
        Assert.Empty(inputs.RootElement.GetProperty("source").EnumerateObject());
        var context = new TaskContext(outputs: new("consumer"), inputs: new Dictionary<string, NodeOutputs>
            { ["source"] = new("source") }, declaredInputs: [new("source", "missing")]);
        Assert.Throws<KeyNotFoundException>(() => context.Input<string>("source", "missing"));
        await runtime.Complete(consumer, TaskOutcome.Failed, "TaskError", true);
        await runtime.Finish();
        Assert.Equal(RunStatus.Failed, (await runtime.Load()).Snapshot.Status);
    }

    [Fact]
    public async Task Soft_failure_remains_visible_but_does_not_fail_the_run()
    {
        await using var runtime = await Runtime.Create(database,
            w => w.Node("work", n => n.Task("soft", _ => { }, failure: Failure.Soft)));
        await runtime.Tick();
        await runtime.Complete(Assert.Single(await runtime.Pending()), TaskOutcome.Failed, "TaskError", true);
        await runtime.Finish();
        var snapshot = (await runtime.Load()).Snapshot;
        Assert.Equal(ExecutionStatus.SoftFailed, snapshot.Tasks["work/soft"]);
        Assert.Equal(ExecutionStatus.SoftFailed, snapshot.Nodes["work"]);
        Assert.Equal(RunStatus.Succeeded, snapshot.Status);
    }

    [Fact]
    public async Task Hard_parallel_failure_cancels_real_active_identity_before_cleanup_can_start()
    {
        await using var runtime = await Runtime.Create(database, w => w
            .Node("work", n => n.Parallel(2).Task("failure", _ => { }).Task("sibling", _ => { }))
            .PostProcess(_ => { }));
        await runtime.Tick();
        var commands = await runtime.Pending();
        Assert.Equal(2, commands.Count);
        var sibling = Assert.Single(commands, command => command.Task.TaskKey == "sibling");
        await runtime.Complete(Assert.Single(commands, command => command.Task.TaskKey == "failure"), TaskOutcome.Failed, "TaskError", true);
        await runtime.Tick();
        var cancel = Assert.Single(await runtime.Store.PendingCancelsAsync(runtime.Session, Token));
        Assert.Equal(sibling.Task, cancel.Task);
        Assert.Equal(ExecutionStatus.Pending, (await runtime.Load()).Snapshot.Nodes["$post"]);
        await runtime.Complete(sibling, TaskOutcome.Cancelled, "Aborted", true);
        await runtime.Finish();
        Assert.Equal(RunStatus.Failed, (await runtime.Load()).Snapshot.Status);
    }

    [Fact]
    public async Task Accepted_abort_cancels_current_attempt_and_passes_original_record_to_bounded_cleanup()
    {
        await using var runtime = await Runtime.Create(database, w => w
            .Node("work", n => n.Task("task", _ => { })).PostProcess(_ => { }).SystemPostProcess(_ => { }));
        await runtime.Tick();
        var active = Assert.Single(await runtime.Pending());
        var run = await runtime.Load();
        var record = new AbortRecord("actor", "Operator", runtime.Clock.Now, "stop prototype run", AbortSource.API);
        await runtime.Store.AppendAsync(run,
            run.CreateEvent(EventEntity.Run, null, (int)RunStatus.Aborting, runtime.Clock.Now, record), Token);
        await runtime.Tick();
        Assert.Equal(active.Task, Assert.Single(await runtime.Store.PendingCancelsAsync(runtime.Session, Token)).Task);
        await runtime.Complete(active, TaskOutcome.Cancelled, "Aborted", true);
        await runtime.Tick();
        var cleanup = Assert.Single(await runtime.Pending());
        Assert.Equal("$post", cleanup.Task.NodeKey);
        Assert.Equal(record, JsonSerializer.Deserialize(cleanup.AbortJson.Span, AbortRecordJsonContext.Default.AbortRecord));
        Assert.True(cleanup.DeadlineAt.ToDateTimeOffset() > runtime.Clock.Now);
        await runtime.Complete(cleanup);
        await runtime.Finish();
        var snapshot = (await runtime.Load()).Snapshot;
        Assert.Equal(RunStatus.Aborted, snapshot.Status);
        Assert.Equal(record, snapshot.Abort);
        Assert.All(snapshot.Tasks.Values, status => Assert.True(TransitionTable.IsTerminal(status)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Expired_run_or_node_deadline_waits_for_physical_completion_then_runs_bounded_cleanup(bool nodeTimeout)
    {
        await using var runtime = await Runtime.Create(database, w =>
        {
            w.RunTimeout(nodeTimeout ? TimeSpan.FromMinutes(1) : TimeSpan.FromSeconds(1));
            w.Node("work", n =>
            {
                if (nodeTimeout) n.Timeout(TimeSpan.FromSeconds(1));
                n.Task("task", _ => { }, p => p.Timeout(TimeSpan.FromSeconds(1)));
            });
            w.PostProcess(_ => { });
        });
        await runtime.Tick();
        var active = Assert.Single(await runtime.Pending());
        runtime.Clock.Now = runtime.Clock.Now.AddSeconds(2);
        await runtime.Tick();
        Assert.Equal(ExecutionStatus.Pending, (await runtime.Load()).Snapshot.Nodes["$post"]);
        Assert.Single(await runtime.Store.PendingCancelsAsync(runtime.Session, Token));
        await runtime.Complete(active, TaskOutcome.Cancelled, "Aborted", true);
        await runtime.Tick();
        var cleanup = Assert.Single(await runtime.Pending());
        Assert.Equal("$post", cleanup.Task.NodeKey);
        Assert.True(cleanup.DeadlineAt.ToDateTimeOffset() > runtime.Clock.Now);
        await runtime.Complete(cleanup);
        await runtime.Finish();
        var snapshot = (await runtime.Load()).Snapshot;
        Assert.Equal(ExecutionStatus.TimedOut, snapshot.Tasks["work/task"]);
        Assert.Equal(nodeTimeout ? RunStatus.Failed : RunStatus.TimedOut, snapshot.Status);
    }

    [Fact]
    public async Task Failed_outbox_transaction_rolls_back_lifecycle_and_placement_then_retries_from_durable_state()
    {
        await using var runtime = await Runtime.Create(database, w => w.Node("work", n => n.Task("task", _ => { })));
        var revision = runtime.Placement.Snapshot.Revision;
        await runtime.Execute($"ALTER TABLE dispatch_outbox ADD CONSTRAINT hosted_test_failure CHECK (run_id<>'{runtime.RunId:D}')");
        try
        {
            await Assert.ThrowsAsync<PostgresException>(runtime.Tick);
            Assert.Equal(0, (await runtime.Load()).Snapshot.LastSequence);
            Assert.Equal(revision, runtime.Placement.Snapshot.Revision);
            Assert.Equal(0L, await runtime.Scalar("SELECT count(*) FROM task_attempts WHERE run_id=$1"));
        }
        finally { await runtime.Execute("ALTER TABLE dispatch_outbox DROP CONSTRAINT hosted_test_failure"); }
        await runtime.Finish();
        Assert.Equal(RunStatus.Succeeded, (await runtime.Load()).Snapshot.Status);
    }

    [Fact]
    public async Task Already_accepted_run_keeps_its_pinned_package_after_workflow_soft_delete()
    {
        await using var runtime = await Runtime.Create(database, w => w.Node("work", n => n.Task("task", _ => { })));
        await runtime.Execute("UPDATE workflows SET deleted_at=clock_timestamp() WHERE id=$1", runtime.Workflow);
        await runtime.Tick();
        var command = Assert.Single(await runtime.Pending());
        Assert.Equal("1.0.0", command.Version);
        Assert.Contains("/api/v1/packages/", command.PackageUri);
        await runtime.Complete(command);
        await runtime.Finish();
        Assert.Equal(RunStatus.Succeeded, (await runtime.Load()).Snapshot.Status);
    }

    internal sealed class Runtime : IAsyncDisposable
    {
        private readonly NpgsqlDataSource source;
        private readonly AgentRegistry agents;
        private readonly Dispatcher dispatcher;
        private readonly LeaseManager leadership;
        private readonly RunRecovery recovery;
        private readonly RunExecutionService executor;
        private readonly ServiceProvider services;
        public Clock Clock { get; } = new();
        public RunStore Store { get; }
        public PlacementEngine Placement { get; } = new();
        public AgentSession Session { get; private set; }
        public Guid RunId { get; private set; }
        public string Workflow { get; private set; } = "";
        private CancellationToken Token => TestContext.Current.CancellationToken;

        private Runtime(NpgsqlDataSource source)
        {
            this.source = source;
            var options = new RunExecutionOptions(new Uri("http://localhost:5000"));
            Store = new(source, NullLogger<RunStore>.Instance, timeProvider: Clock, execution: options);
            agents = new(source, Clock);
            dispatcher = new(source, Store, agents, Placement, Clock);
            leadership = new(source, new("hosted-unit", new Uri("http://localhost:5000")), NullLogger<LeaseManager>.Instance, Clock);
            recovery = new(source, Store, leadership, Clock, NullLogger<RunRecovery>.Instance);
            services = new ServiceCollection().AddSingleton<TimeProvider>(Clock)
                .AddPackageDownloads(RandomNumberGenerator.GetBytes(32)).BuildServiceProvider();
            executor = new(source, recovery, dispatcher, new(source, Store, Clock), Clock, options,
                services.GetRequiredKeyedService<BlobUploadGrants>(PackageDownloadEndpoints.Grants),
                NullLogger<RunExecutionService>.Instance);
        }

        public static async Task<Runtime> Create(HostedExecutionDatabase database, Action<WorkflowBuilder> configure,
            RunPlacement? placement = null, int maxTasks = 4, string? agentName = null, Guid? agentId = null, string? teamName = null)
        {
            var runtime = new Runtime(database.DataSource);
            var team = teamName ?? "hosted-" + Guid.NewGuid().ToString("N");
            var definition = Wf.Sdk.Workflow.Define(team + "/test/workflow", "1.0.0", w =>
            {
                w.Type("test").RunsOn(Machines.Pool(team));
                configure(w);
            });
            runtime.Workflow = definition.Name;
            await using (var connection = await database.DataSource.OpenConnectionAsync(runtime.Token))
            await using (var transaction = await connection.BeginTransactionAsync(runtime.Token))
            {
                await new RegistryWriter().UpsertAsync(connection, transaction,
                    new(team, [new(team, "test", "Hosted execution", new([]))]), runtime.Token);
                await transaction.CommitAsync(runtime.Token);
            }
            var version = Guid.CreateVersion7();
            await runtime.Execute("INSERT INTO workflows(id,type_id) VALUES($1,$2)", definition.Name, team + "/test");
            await runtime.Execute("""
                INSERT INTO workflow_versions(id,workflow_id,semver,manifest,package_uri,package_sha256,package_size,
                    source_uri,source_sha256,source_size,git_commit,git_author,registered_by,sdk_version)
                VALUES($1,$2,'1.0.0',$3::jsonb,'packages/'||repeat('00',32)||'.zip',decode(repeat('00',32),'hex'),1,
                    'sources/'||repeat('00',32),decode(repeat('00',32),'hex'),1,repeat('a',40),'hosted','hosted','1')
                """, version, definition.Name, ManifestWriter.Write(definition));
            runtime.Session = await runtime.agents.RegisterAsync(new()
            {
                Id = (agentId ?? Guid.NewGuid()).ToString("D"), Name = agentName ?? team, Os = "windows", Arch = "x64", Cores = 4,
                RamMb = 16384, MaxTasks = checked((uint)maxTasks), Pool = team, Version = "1.0.0", ProtocolVersion = 1
            }, runtime.Token);
            var run = new RunState(Guid.CreateVersion7(), definition.Nodes, definition.Metadata, Assert.Fail, runtime.Clock.Now);
            run = RunState.Restore(run.Snapshot with { Placement = placement }, [], Assert.Fail);
            runtime.RunId = run.Snapshot.RunId;
            await runtime.Store.CreateAsync(run, definition.Name, version, DateTimeOffset.UtcNow.Add(definition.RunTimeout), runtime.Token);
            return runtime;
        }

        public Task Tick() => executor.TickAsync(Token);
        public async Task FreshTick()
        {
            using var rebuilt = new Dispatcher(source, Store, agents, new PlacementEngine(), Clock);
            using var service = new RunExecutionService(source, recovery, rebuilt, new(source, Store, Clock), Clock,
                new(new Uri("http://localhost:5000")),
                services.GetRequiredKeyedService<BlobUploadGrants>(PackageDownloadEndpoints.Grants),
                NullLogger<RunExecutionService>.Instance);
            await service.TickAsync(Token);
        }

        public Task<AgentSession> AddAgent(string suffix = "other", Guid? id = null, string os = "windows",
            string? pool = null, string? name = null, Dictionary<string, string>? labels = null, int maxTasks = 4)
        {
            var hello = new Hello
            {
                Id = (id ?? Guid.NewGuid()).ToString("D"), Name = name ?? Workflow.Split('/')[0] + "-" + suffix + "-" + Guid.NewGuid().ToString("N"), Os = os,
                Arch = "x64", Cores = 4, RamMb = 16384, MaxTasks = checked((uint)maxTasks), Pool = pool ?? Workflow.Split('/')[0],
                Version = "1.0.0", ProtocolVersion = 1
            };
            if (labels is not null) hello.Labels.Add(labels);
            return agents.RegisterAsync(hello, Token);
        }

        public Task<IReadOnlyList<(long Id, DispatchCommand Command)>> Pending(AgentSession session) =>
            Store.PendingAsync(session, Token);

        public async Task LoseAndReplace(AgentSession session)
        {
            Clock.Now = Clock.Now.AddMilliseconds(1);
            using var sweeper = new Wf.ControlPlane.Engine.Sweeper.AgentLossSweeper(source, agents, Clock,
                NullLogger<Wf.ControlPlane.Engine.Sweeper.AgentLossSweeper>.Instance);
            var lost = await sweeper.ReconcileAsync(session, [], Token);
            await Execute("UPDATE agents SET status=2 WHERE id=$1", session.AgentId);
            using var rebuilt = new Dispatcher(source, Store, agents, new PlacementEngine(), Clock);
            foreach (var attempt in lost)
                await rebuilt.ReplaceLostAsync(attempt.RunId, attempt.TaskKey, attempt.Attempt, Token);
        }
        public Task<RunState> Load() => Store.LoadAsync(RunId, Token);
        public async Task<IReadOnlyList<DispatchCommand>> Pending() =>
            (await Store.PendingAsync(Session, Token)).Select(item => item.Command).ToArray();

        public async Task Complete(DispatchCommand command, TaskOutcome outcome = TaskOutcome.Succeeded,
            string? category = null, bool nonRetryable = false, AgentSession? session = null)
        {
            Clock.Now = Clock.Now.AddMilliseconds(1);
            await Store.AcceptAsync(session ?? Session, new()
            {
                TaskStarted = new() { Task = command.Task.Clone(), At = Timestamp.FromDateTimeOffset(Clock.Now),
                    ReservationEpoch = command.ReservationEpoch }
            }, Token);
            Clock.Now = Clock.Now.AddMilliseconds(1);
            await Store.AcceptAsync(session ?? Session, new()
            {
                TaskCompleted = new()
                {
                    Task = command.Task.Clone(), At = Timestamp.FromDateTimeOffset(Clock.Now), Outcome = outcome,
                    ReservationEpoch = command.ReservationEpoch,
                    Error = category is null ? null : new() { Category = category, Message = "simulated agent result", NonRetryable = nonRetryable }
                }
            }, Token);
        }

        public async Task Finish()
        {
            for (var step = 0; step < 32; step++)
            {
                await Tick();
                if (TransitionTable.IsTerminal((await Load()).Snapshot.Status)) return;
                foreach (var command in await Pending()) await Complete(command);
            }
            Assert.Fail("The durable planner did not reach a terminal state after simulated agent results.");
        }

        public async Task<object?> Scalar(string sql)
        {
            await using var command = source.CreateCommand(sql);
            command.Parameters.AddWithValue(RunId);
            return await command.ExecuteScalarAsync(Token);
        }

        public async Task Execute(string sql, params object[] values)
        {
            await using var command = source.CreateCommand(sql);
            foreach (var value in values) command.Parameters.AddWithValue(value);
            await command.ExecuteNonQueryAsync(Token);
        }

        public async ValueTask DisposeAsync()
        {
            executor.Dispose();
            recovery.Dispose();
            leadership.Dispose();
            dispatcher.Dispose();
            agents.Dispose();
            await services.DisposeAsync();
        }
    }

    internal sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow.AddSeconds(1);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}

public sealed class HostedExecutionDatabase : AgentDatabase
{
    protected override IReadOnlyList<string> Migrations => Migrator.LoadScripts().Select(script => script.Name).ToArray();
}
