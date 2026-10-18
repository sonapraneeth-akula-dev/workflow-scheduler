using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Wf.Contracts;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Definition;
using Wf.Contracts.Events;
using Wf.Contracts.Registry;
using Wf.ControlPlane.Api;
using Wf.ControlPlane.Registry;
using Wf.ControlPlane.Engine.Agents;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Leadership;
using Wf.ControlPlane.Engine.Recovery;
using Wf.ControlPlane.Engine.Sweeper;
using Wf.Engine.Core;
using Wf.Engine.Core.Metrics;
using Wf.Engine.Core.Placement;
using Wf.Integration.Tests.Agents;
using Wf.Sdk;
using Wf.Sdk.Local;
using DispatchCommand = Wf.Contracts.Agent.V1.Dispatch;

namespace Wf.Integration.Tests.Execution;

public sealed class RunMetricsTests(RunMetricsDatabase database) : IClassFixture<RunMetricsDatabase>
{
    private const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private readonly MetricsClock clock = new();
    private RunStore Store() => new(database.DataSource, NullLogger<RunStore>.Instance);

    [Theory]
    [InlineData("TaskError", FailureCategory.TaskError)]
    [InlineData("Timeout", FailureCategory.Timeout)]
    [InlineData("AgentLost", FailureCategory.AgentLost)]
    [InlineData("PlatformError", FailureCategory.PlatformError)]
    [InlineData("ValidationError", FailureCategory.ValidationError)]
    [InlineData("Aborted", FailureCategory.Aborted)]
    [InlineData("ResourceLimit", FailureCategory.ResourceLimit)]
    [InlineData("Placement", FailureCategory.Placement)]
    [InlineData("", null)]
    [InlineData("unknown-producer-category", null)]
    [InlineData("0", null)]
    [InlineData("taskerror", null)]
    public async Task Actual_gateway_preserves_explicit_categories_in_committed_events_attempts_replay_and_HTTP(
        string wireCategory, FailureCategory? expected)
    {
        var store = Store();
        using var registry = new AgentRegistry(database.DataSource, clock);
        await using var gateway = Gateway(registry, store);
        await gateway.StartAsync(Token);
        using var channel = GrpcChannel.ForAddress(gateway.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.Single());
        using var call = new AgentGateway.AgentGatewayClient(channel).Connect(cancellationToken: Token);
        await call.RequestStream.WriteAsync(new AgentMessage { Sequence = 1, Hello = Hello() }, Token);
        await Ack(1);
        var team = "metrics-wire-" + Guid.NewGuid().ToString("N");
        var group = Guid.NewGuid();
        await using (var connection = await database.DataSource.OpenConnectionAsync(Token))
        await using (var transaction = await connection.BeginTransactionAsync(Token))
        {
            await new RegistryWriter().UpsertAsync(connection, transaction,
                new(team, [new(team, "metrics", "Wire categories", new([]))],
                    RoleBindings: [new(RegistryRole.Viewer, group)]), Token);
            await transaction.CommitAsync(Token);
        }
        var run = new RunState(Guid.CreateVersion7(), [new("node", [], [new("task")])],
            new("metrics", ImmutableSortedDictionary<string, string>.Empty), Assert.Fail);
        await store.CreateAsync(run, $"{team}/metrics/wire", Guid.CreateVersion7(), DateTimeOffset.UtcNow.AddHours(1), Token);
        var initial = (await store.LoadAsync(run.Snapshot.RunId, Token)).Snapshot;
        clock.Advance(1);
        await Append(store, run, EventEntity.Run, null, (int)RunStatus.Running);
        await Append(store, run, EventEntity.Task, "node/task", (int)ExecutionStatus.Ready);
        using var dispatcher = new Dispatcher(database.DataSource, store, registry, new PlacementEngine(), clock);
        await dispatcher.DispatchAsync(run, Request(run, 1), Command(run, 1), Token);
        clock.Advance(1);
        await call.RequestStream.WriteAsync(new AgentMessage { Sequence = 2, TaskStarted = new()
            { Task = Command(run, 1).Task, At = Timestamp.FromDateTimeOffset(clock.GetUtcNow()) } }, Token);
        await Ack(2);
        clock.Advance(2);
        var completed = new AgentMessage { Sequence = 3, TaskCompleted = new()
        {
            Task = Command(run, 1).Task, At = Timestamp.FromDateTimeOffset(clock.GetUtcNow()),
            Outcome = TaskOutcome.Failed,
            Error = new() { Category = wireCategory, Message = "private-scripted-producer-message", NonRetryable = true }
        } };
        await call.RequestStream.WriteAsync(completed, Token);
        await Ack(3);
        var saved = await store.LoadAsync(run.Snapshot.RunId, Token);
        var metric = saved.Snapshot.Metrics!.Tasks["node/task"];
        Assert.Equal(expected, metric.FailureCategory);
        Assert.Equal(expected, Assert.Single(metric.Attempts).FailureCategory);
        Assert.Equal(expected is null ? null : (short?)expected,
            await Scalar<short?>(run, "SELECT failure_category FROM task_attempts WHERE run_id=$1 AND attempt=1"));
        Assert.Equal(expected, (await Events(run))[^1].FailureCategory);
        AssertMetrics(saved.Snapshot.Metrics, RunState.Restore(initial, await Events(run), Assert.Fail).Snapshot.Metrics!);
        completed.Sequence = 4;
        await call.RequestStream.WriteAsync(completed, Token);
        await Ack(4);
        Assert.Equal(saved.Snapshot.LastSequence, (await store.LoadAsync(run.Snapshot.RunId, Token)).Snapshot.LastSequence);
        await using var api = await MetricsApiServer.Start(database.DataSource, Token);
        using var response = await api.Get($"/api/v1/runs/{run.Snapshot.RunId:D}/metrics", api.Jwt(group), Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync(Token);
        Assert.DoesNotContain("private-scripted-producer-message", json);
        var output = JsonSerializer.Deserialize(json, MetricsJsonContext.Default.RunMetricsResponse)!;
        Assert.Equal(expected?.ToString(), output.Tasks["node/task"].FailureCategory);
        Assert.Equal(expected?.ToString(), Assert.Single(output.Tasks["node/task"].Attempts).FailureCategory);
        Assert.Equal(2000, output.Tasks["node/task"].DurationMs);
        Assert.Equal(0, output.Run.RetryCount);

        async Task Ack(ulong expectedSequence)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            while (await call.ResponseStream.MoveNext(timeout.Token))
                if (call.ResponseStream.Current.Ack?.Sequence == expectedSequence) return;
            Assert.Fail($"Gateway closed before acknowledgement {expectedSequence}.");
        }
    }

    [Theory]
    [InlineData(TaskOutcome.Succeeded, ExecutionStatus.Succeeded)]
    [InlineData(TaskOutcome.Failed, ExecutionStatus.Failed)]
    [InlineData(TaskOutcome.Cancelled, ExecutionStatus.Cancelled)]
    [InlineData(TaskOutcome.TimedOut, ExecutionStatus.TimedOut)]
    public async Task Real_gateway_commits_attempt_wait_start_end_status_and_replays_without_double_counting(
        TaskOutcome outcome, ExecutionStatus terminal)
    {
        var store = Store();
        using var registry = new AgentRegistry(database.DataSource, clock);
        await using var gateway = Gateway(registry, store);
        await gateway.StartAsync(Token);
        using var channel = GrpcChannel.ForAddress(gateway.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.Single());
        using var call = new AgentGateway.AgentGatewayClient(channel).Connect(cancellationToken: Token);
        var hello = Hello();
        await call.RequestStream.WriteAsync(new AgentMessage { Sequence = 1, Hello = hello }, Token);
        await Ack(1);
        var run = await Create(store);
        var initial = run.Snapshot;
        clock.Advance(1);
        await Append(store, run, EventEntity.Run, null, (int)RunStatus.Running);
        await Append(store, run, EventEntity.Node, "node", (int)ExecutionStatus.Ready);
        await Append(store, run, EventEntity.Task, "node/task", (int)ExecutionStatus.Ready);
        clock.Advance(2);
        using var dispatcher = new Dispatcher(database.DataSource, store, registry, new PlacementEngine(), clock);
        await dispatcher.DispatchAsync(run, Request(run, 1), Command(run, 1), Token);
        Assert.Equal(2000, await Scalar<double>(run, "SELECT queue_wait_ms FROM task_attempts WHERE run_id=$1"));
        clock.Advance(4);
        await Append(store, run, EventEntity.Node, "node", (int)ExecutionStatus.Dispatched);
        await Append(store, run, EventEntity.Node, "node", (int)ExecutionStatus.Running);
        var started = new AgentMessage { Sequence = 2, TaskStarted = new()
            { Task = Command(run, 1).Task, At = Timestamp.FromDateTimeOffset(clock.GetUtcNow()) } };
        await call.RequestStream.WriteAsync(started, Token);
        await Ack(2);
        clock.Advance(5);
        var completed = new AgentMessage { Sequence = 3, TaskCompleted = new()
            { Task = Command(run, 1).Task, At = Timestamp.FromDateTimeOffset(clock.GetUtcNow()), Outcome = outcome } };
        await call.RequestStream.WriteAsync(completed, Token);
        await Ack(3);
        var after = await store.LoadAsync(run.Snapshot.RunId, Token);
        var metric = after.Snapshot.Metrics!.Tasks["node/task"];
        Assert.Equal(6000, metric.QueueWaitMs);
        Assert.Equal(5000, metric.DurationMs);
        Assert.Equal((int)terminal, metric.TerminalStatus);
        Assert.Equal(6000, await Scalar<double>(run, "SELECT queue_wait_ms FROM task_attempts WHERE run_id=$1"));
        Assert.Equal((short)terminal, await Scalar<short>(run, "SELECT status FROM task_attempts WHERE run_id=$1"));
        Assert.Equal(metric.StartedAt, await Scalar<DateTimeOffset>(run, "SELECT started_at FROM task_attempts WHERE run_id=$1"));
        Assert.Equal(metric.EndedAt, await Scalar<DateTimeOffset>(run, "SELECT ended_at FROM task_attempts WHERE run_id=$1"));
        var sequence = after.Snapshot.LastSequence;
        completed.Sequence = 4;
        await call.RequestStream.WriteAsync(completed, Token);
        await Ack(4);
        Assert.Equal(sequence, (await store.LoadAsync(run.Snapshot.RunId, Token)).Snapshot.LastSequence);
        await Append(store, after, EventEntity.Node, "node", (int)terminal);
        if (terminal == ExecutionStatus.Cancelled)
            await Append(store, after, EventEntity.Run, null, (int)RunStatus.Aborting);
        var runStatus = terminal switch
        {
            ExecutionStatus.Succeeded => RunStatus.Succeeded,
            ExecutionStatus.Cancelled => RunStatus.Aborted,
            ExecutionStatus.TimedOut => RunStatus.TimedOut,
            _ => RunStatus.Failed
        };
        await Append(store, after, EventEntity.Run, null, (int)runStatus);
        var saved = await store.LoadAsync(run.Snapshot.RunId, Token);
        Assert.Equal(saved.Snapshot.Metrics!.Run.StartedAt,
            await Scalar<DateTimeOffset>(run, "SELECT started_at FROM runs WHERE id=$1"));
        Assert.Equal(saved.Snapshot.Metrics.Nodes["node"].StartedAt,
            await Scalar<DateTimeOffset>(run, "SELECT started_at FROM node_runs WHERE run_id=$1"));
        Assert.Equal(saved.Snapshot.Metrics.Nodes["node"].EndedAt,
            await Scalar<DateTimeOffset>(run, "SELECT ended_at FROM node_runs WHERE run_id=$1"));
        AssertMetrics(saved.Snapshot.Metrics, RunState.Restore(initial, await Events(run), Assert.Fail).Snapshot.Metrics!);

        async Task Ack(ulong expected)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            while (await call.ResponseStream.MoveNext(timeout.Token))
                if (call.ResponseStream.Current.Ack?.Sequence == expected) return;
            Assert.Fail($"Gateway closed before acknowledgement {expected}.");
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Actual_postgres_agent_loss_preserves_attempt_end_wait_and_independent_retry_budgets(
        bool autoRetry, bool started)
    {
        var store = Store();
        using var registry = new AgentRegistry(database.DataSource, clock);
        var session = await registry.RegisterAsync(Hello(), Token);
        using var dispatcher = new Dispatcher(database.DataSource, store, registry, new PlacementEngine(), clock);
        using var sweeper = new AgentLossSweeper(database.DataSource, registry, clock, NullLogger<AgentLossSweeper>.Instance);
        var run = await Create(store);
        var initial = run.Snapshot;
        await Append(store, run, EventEntity.Run, null, (int)RunStatus.Running);
        await Append(store, run, EventEntity.Task, "node/task", (int)ExecutionStatus.Ready);
        var policy = new ExecutionPolicy(TimeSpan.FromMinutes(5),
            new RetryPolicy(7, on: [FailureCategory.AgentLost]), autoRetry);
        clock.Advance(2);
        await dispatcher.DispatchAsync(run, Request(run, 1), Command(run, 1), policy, Token);
        var last = autoRetry ? 3 : 1;
        for (short number = 1; number <= last; number++)
        {
            clock.Advance(3);
            if (started)
                await store.AcceptAsync(session, new AgentMessage { TaskStarted = new()
                    { Task = Command(run, number).Task, At = Timestamp.FromDateTimeOffset(clock.GetUtcNow()) } }, Token);
            clock.Advance(4);
            Assert.Contains(await sweeper.ReconcileAsync(session, [], Token), a => a.RunId == run.Snapshot.RunId);
            var lostAt = clock.GetUtcNow();
            clock.Advance(9);
            await registry.RegisterAsync(Hello(), Token);
            Assert.Equal(number < last ? AgentLossDisposition.Replaced : AgentLossDisposition.Failed,
                await dispatcher.ReplaceLostAsync(run.Snapshot.RunId, "node/task", number, Token));
            run = await store.LoadAsync(run.Snapshot.RunId, Token);
            var metric = run.Snapshot.Metrics!.Tasks["node/task"];
            Assert.Equal(lostAt, metric.Attempts[number - 1].EndedAt);
            Assert.Equal(started ? 4000 : 0, metric.Attempts[number - 1].DurationMs);
            Assert.Equal(FailureCategory.AgentLost, metric.Attempts[number - 1].FailureCategory);
            Assert.Equal(started ? number == 1 ? 5000 : 12000 : number == 1 ? 9000 : 16000,
                metric.Attempts[number - 1].QueueWaitMs);
            if (number < last)
            {
                var id = await Scalar<Guid>(run, $"SELECT agent_id FROM task_attempts WHERE run_id=$1 AND attempt={number + 1}");
                session = new(id, registry.Find(id)!.SessionId);
            }
        }
        var metrics = run.Snapshot.Metrics!;
        Assert.Equal(last - 1, metrics.Tasks["node/task"].RetryCount);
        Assert.Equal(last - 1, metrics.Run.RetryCount);
        Assert.Equal(last - 1, metrics.Nodes["node"].RetryCount);
        Assert.Equal((int)ExecutionStatus.Failed, metrics.Tasks["node/task"].TerminalStatus);
        Assert.Equal(0L, await Scalar<long>(run,
            "SELECT count(*) FROM task_attempts WHERE run_id=$1 AND (queue_wait_ms IS NULL OR failure_category<>2 OR author_retries<>0)"));
        Assert.Equal((short)(last - 1), await Scalar<short>(run,
            "SELECT max(platform_retries) FROM task_attempts WHERE run_id=$1"));
        AssertMetrics(metrics, RunState.Restore(initial, await Events(run), Assert.Fail).Snapshot.Metrics!);
    }

    [Fact]
    public async Task Actual_storage_failure_rolls_back_metrics_snapshot_events_and_attempt_times()
    {
        var store = Store();
        using var registry = new AgentRegistry(database.DataSource, clock);
        await registry.RegisterAsync(Hello(), Token);
        using var dispatcher = new Dispatcher(database.DataSource, store, registry, new PlacementEngine(), clock);
        var run = await Create(store);
        await Append(store, run, EventEntity.Run, null, (int)RunStatus.Running);
        await Append(store, run, EventEntity.Task, "node/task", (int)ExecutionStatus.Ready);
        clock.Advance(2);
        await dispatcher.DispatchAsync(run, Request(run, 1), Command(run, 1), Token);
        var before = run.Snapshot;
        await Execute($"ALTER TABLE task_attempts ADD CONSTRAINT reject_metrics_start CHECK (run_id<>'{run.Snapshot.RunId:D}' OR started_at IS NULL) NOT VALID");
        try
        {
            clock.Advance(5);
            await Assert.ThrowsAsync<PostgresException>(() =>
                Append(store, run, EventEntity.Task, "node/task", (int)ExecutionStatus.Running));
            Assert.Same(before, run.Snapshot);
            var loaded = await store.LoadAsync(run.Snapshot.RunId, Token);
            Assert.Equal(before.LastSequence, loaded.Snapshot.LastSequence);
            AssertMetrics(before.Metrics!, loaded.Snapshot.Metrics!);
            Assert.Equal(2000, await Scalar<double>(run, "SELECT queue_wait_ms FROM task_attempts WHERE run_id=$1"));
            Assert.Null(await Scalar<object?>(run, "SELECT started_at FROM task_attempts WHERE run_id=$1"));
        }
        finally { await Execute("ALTER TABLE task_attempts DROP CONSTRAINT reject_metrics_start"); }
    }

    [Fact]
    public async Task Durable_attempt_identity_not_event_count_selects_the_attempt_row_after_restart()
    {
        var store = Store();
        using var registry = new AgentRegistry(database.DataSource, clock);
        var session = await registry.RegisterAsync(Hello(), Token);
        using var dispatcher = new Dispatcher(database.DataSource, store, registry, new PlacementEngine(), clock);
        var run = await Create(store);
        await Append(store, run, EventEntity.Run, null, (int)RunStatus.Running);
        await Append(store, run, EventEntity.Task, "node/task", (int)ExecutionStatus.Ready);
        clock.Advance(2);
        await dispatcher.DispatchAsync(run, Request(run, 5), Command(run, 5), Token);
        clock.Advance(3);
        await Store().AcceptAsync(session, new() { TaskStarted = new()
            { Task = Command(run, 5).Task, At = Timestamp.FromDateTimeOffset(clock.GetUtcNow()) } }, Token);
        clock.Advance(7);
        await Store().AcceptAsync(session, new() { TaskCompleted = new()
            { Task = Command(run, 5).Task, At = Timestamp.FromDateTimeOffset(clock.GetUtcNow()), Outcome = TaskOutcome.Succeeded } }, Token);
        var metric = (await store.LoadAsync(run.Snapshot.RunId, Token)).Snapshot.Metrics!.Tasks["node/task"];
        Assert.Equal(5, Assert.Single(metric.Attempts).Attempt);
        Assert.Equal(0, metric.RetryCount);
        Assert.Equal(5000, metric.QueueWaitMs);
        Assert.Equal(7000, metric.DurationMs);
        Assert.Equal(5000, await Scalar<double>(run, "SELECT queue_wait_ms FROM task_attempts WHERE run_id=$1 AND attempt=5"));
    }

    [Fact]
    public async Task Legacy_snapshots_rebuild_existing_attempt_history_before_recording_new_attempts()
    {
        var store = Store();
        var run = await Create(store);
        var initial = run.Snapshot;
        await Append(store, run, EventEntity.Run, null, (int)RunStatus.Running);
        await Append(store, run, EventEntity.Task, "node/task", (int)ExecutionStatus.Ready);
        clock.Advance(1);
        await Append(store, run, EventEntity.Task, "node/task", (int)ExecutionStatus.Dispatched);
        clock.Advance(2);
        await Append(store, run, EventEntity.Task, "node/task", (int)ExecutionStatus.Running);
        clock.Advance(4);
        await Append(store, run, EventEntity.Task, "node/task", (int)ExecutionStatus.Retrying);
        await Execute("""
            UPDATE run_snapshots SET payload=convert_to((convert_from(payload,'UTF8')::jsonb-'Metrics')::text,'UTF8')
            WHERE run_id=$1
            """, run.Snapshot.RunId);
        var restored = await store.LoadAsync(run.Snapshot.RunId, Token);
        AssertMetrics(run.Snapshot.Metrics!, restored.Snapshot.Metrics!);
        clock.Advance(10);
        await Append(store, restored, EventEntity.Task, "node/task", (int)ExecutionStatus.Dispatched);
        Assert.Equal(1, restored.Snapshot.Metrics!.Tasks["node/task"].RetryCount);
        Assert.Equal(2, restored.Snapshot.Metrics.Tasks["node/task"].Attempts.Length);
        AssertMetrics(restored.Snapshot.Metrics, RunState.Restore(initial, await Events(run), Assert.Fail).Snapshot.Metrics!);
    }

    [Fact]
    public async Task Legacy_history_without_metric_event_fields_rebuilds_identically_in_actual_batched_recovery()
    {
        var store = Store();
        var run = await Create(store);
        var initial = run.Snapshot;
        await Append(store, run, EventEntity.Run, null, (int)RunStatus.Running);
        await Append(store, run, EventEntity.Task, "node/task", (int)ExecutionStatus.Ready);
        clock.Advance(1);
        await Append(store, run, EventEntity.Task, "node/task", (int)ExecutionStatus.Dispatched);
        clock.Advance(2);
        await Append(store, run, EventEntity.Task, "node/task", (int)ExecutionStatus.Running);
        clock.Advance(4);
        await Append(store, run, EventEntity.Task, "node/task", (int)ExecutionStatus.Retrying);
        await Execute("""
            WITH snapshots AS (
                UPDATE run_snapshots SET payload=convert_to((convert_from(payload,'UTF8')::jsonb-'Metrics')::text,'UTF8')
                WHERE run_id=$1 RETURNING run_id
            )
            UPDATE run_events SET payload=convert_to((convert_from(payload,'UTF8')::jsonb
                -'RunQueuedAt'-'Attempt'-'AttemptEndedAt'-'AttemptStatus')::text,'UTF8')
            WHERE run_id IN(SELECT run_id FROM snapshots)
            """, run.Snapshot.RunId);
        var queuedAt = await Scalar<DateTimeOffset>(run, "SELECT created_at FROM runs WHERE id=$1");
        initial = initial with { Metrics = RunMetricsRecorder.Initialize(initial, queuedAt) };
        var expected = RunState.Restore(initial, await Events(run), Assert.Fail);
        AssertMetrics(expected.Snapshot.Metrics!, (await store.LoadAsync(run.Snapshot.RunId, Token)).Snapshot.Metrics!);
        using var leader = new LeaseManager(database.DataSource,
            new(Guid.NewGuid().ToString("N"), new Uri("http://localhost:5200")), NullLogger<LeaseManager>.Instance);
        Assert.True(await leader.TryAcquireOrRenewAsync(Token));
        using var recovery = new RunRecovery(database.DataSource, store, leader, clock, NullLogger<RunRecovery>.Instance);
        await recovery.RecoverAsync(Token);
        var recovered = recovery.RecoveredRuns[run.Snapshot.RunId].State;
        Assert.Equal(expected.Snapshot.LastSequence, recovered.Snapshot.LastSequence);
        AssertMetrics(expected.Snapshot.Metrics!, recovered.Snapshot.Metrics!);
        clock.Advance(10);
        await Append(store, recovered, EventEntity.Task, "node/task", (int)ExecutionStatus.Dispatched);
        Assert.Equal(1, recovered.Snapshot.Metrics!.Tasks["node/task"].RetryCount);
        Assert.Equal(2, recovered.Snapshot.Metrics.Tasks["node/task"].Attempts.Length);
    }

    [Fact]
    public async Task Actual_queued_abort_records_durable_queue_wait_without_start_or_execution()
    {
        var store = Store();
        var run = await Create(store);
        var initial = (await store.LoadAsync(run.Snapshot.RunId, Token)).Snapshot;
        clock.Advance(30);
        await Append(store, run, EventEntity.Run, null, (int)RunStatus.Aborted);
        var metric = run.Snapshot.Metrics!.Run;
        Assert.Equal((clock.GetUtcNow() - initial.Metrics!.Run.QueuedAt!.Value).TotalMilliseconds, metric.QueueWaitMs);
        Assert.True(metric.QueueWaitMs > 29000);
        Assert.Equal(0, metric.DurationMs);
        Assert.Null(metric.StartedAt);
        Assert.Null(await Scalar<object?>(run, "SELECT started_at FROM runs WHERE id=$1"));
        Assert.Equal(clock.GetUtcNow(), await Scalar<DateTimeOffset>(run, "SELECT ended_at FROM runs WHERE id=$1"));
        Assert.Equal((int)RunStatus.Aborted, metric.TerminalStatus);
        AssertMetrics(run.Snapshot.Metrics, (await store.LoadAsync(run.Snapshot.RunId, Token)).Snapshot.Metrics!);
    }

    [Fact]
    public async Task Actual_http_retry_and_local_stages_record_committed_metrics_without_changing_policy()
    {
        using var telemetry = new TelemetryMeasurements("run-metrics-local");
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var http = builder.Build();
        var requests = 0;
        http.MapGet("/work", () => Interlocked.Increment(ref requests) == 1
            ? Results.StatusCode(503) : Results.Ok());
        await http.StartAsync(Token);
        var workflow = Workflow.Define("run-metrics-local", "1", w => w.Type("test")
            .SystemInit(_ => { }).PreSetup(_ => { })
            .Node("node", n => n.Task("http", Tasks.Http(HttpMethod.Get, http.Urls.Single() + "/work"),
                p => p.Retry(1, Backoff.Fixed(TimeSpan.FromMilliseconds(25))).NoAutoRetry()))
            .PostProcess(_ => { }).SystemPostProcess(_ => { }));
        var result = await new LocalEngine(workflow, output: TextWriter.Null).RunAsync(Token)
            .WaitAsync(TimeSpan.FromSeconds(15), Token);
        Assert.Equal(2, requests);
        Assert.Equal(RunStatus.Succeeded, result.Snapshot.Status);
        var metrics = result.Snapshot.Metrics!;
        Assert.Equal(1, metrics.Run.RetryCount);
        Assert.Equal(1, metrics.Nodes["node"].RetryCount);
        Assert.Equal(1, metrics.Tasks["node/http"].RetryCount);
        Assert.Equal([1d], telemetry.Values("wf.run.completed"));
        Assert.Equal([1d], telemetry.Values("wf.run.retries"));
        Assert.Equal([metrics.Run.DurationMs / 1000], telemetry.Values("wf.run.duration"));
        Assert.Equal(2, metrics.Tasks["node/http"].Attempts.Length);
        Assert.True(metrics.Tasks["node/http"].Attempts[1].QueueWaitMs >= 20);
        Assert.All(LifecycleStages.Before.Concat(LifecycleStages.After), key =>
        {
            Assert.Equal((int)ExecutionStatus.Succeeded, metrics.Nodes[key].TerminalStatus);
            Assert.NotNull(metrics.Nodes[key].StartedAt);
            Assert.NotNull(metrics.Nodes[key].EndedAt);
        });
        Assert.All(metrics.Tasks.Values.SelectMany(m => m.Attempts), attempt =>
        {
            Assert.Equal((attempt.EndedAt!.Value - attempt.StartedAt!.Value).TotalMilliseconds, attempt.DurationMs);
            Assert.Equal((attempt.StartedAt.Value - attempt.QueuedAt).TotalMilliseconds, attempt.QueueWaitMs);
        });
        var replay = new RunState(result.Snapshot.RunId, workflow.Nodes, workflow.Metadata, Assert.Fail);
        foreach (var change in result.Events) replay.Apply(change);
        AssertMetrics(metrics, replay.Snapshot.Metrics!);
    }

    private async Task<RunState> Create(RunStore store)
    {
        var run = new RunState(Guid.CreateVersion7(), [new("node", [], [new("task")])],
            new("test", ImmutableSortedDictionary<string, string>.Empty), Assert.Fail, queuedAt: clock.GetUtcNow());
        await store.CreateAsync(run, "tests/run-metrics", Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours(1), Token);
        return run;
    }

    private Task Append(RunStore store, RunState run, EventEntity entity, string? key, int state) =>
        store.AppendAsync(run, entity, key, state, clock.GetUtcNow(), Token);
    private static PlacementRequest Request(RunState run, short attempt) => new(run.Snapshot.RunId, "node", "task", attempt, Sha);
    private DispatchCommand Command(RunState run, short attempt) => new()
    {
        Task = new() { RunId = run.Snapshot.RunId.ToString("D"), NodeKey = "node", TaskKey = "task", Attempt = (uint)attempt },
        Version = "1", PackageUri = "https://packages.invalid/package.zip", Sha256 = Sha,
        DeadlineAt = Timestamp.FromDateTimeOffset(clock.GetUtcNow().AddMinutes(5))
    };

    private static Hello Hello()
    {
        var id = Guid.NewGuid();
        return new() { Id = id.ToString("D"), Name = $"metrics-{id:N}", Os = "linux", Arch = "x64",
            Cores = 2, RamMb = 8192, MaxTasks = 10, Pool = "ci", Version = "1", ProtocolVersion = 1 };
    }

    private static WebApplication Gateway(AgentRegistry registry, RunStore store)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(registry);
        builder.Services.AddSingleton(store);
        var app = builder.Build();
        app.MapGrpcService<AgentGatewayService>();
        return app;
    }

    private async Task<IReadOnlyList<RunEvent>> Events(RunState run)
    {
        await using var command = database.DataSource.CreateCommand("SELECT payload FROM run_events WHERE run_id=$1 ORDER BY seq");
        command.Parameters.AddWithValue(run.Snapshot.RunId);
        await using var reader = await command.ExecuteReaderAsync(Token);
        var events = new List<RunEvent>();
        while (await reader.ReadAsync(Token))
            events.Add(JsonSerializer.Deserialize<RunEvent>(reader.GetFieldValue<byte[]>(0))!);
        return events;
    }

    private async Task<T> Scalar<T>(RunState run, string sql)
    {
        await using var command = database.DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(run.Snapshot.RunId);
        await using var reader = await command.ExecuteReaderAsync(Token);
        Assert.True(await reader.ReadAsync(Token));
        return reader.IsDBNull(0) ? default! : reader.GetFieldValue<T>(0);
    }

    private async Task Execute(string sql, params object[] values)
    {
        await using var command = database.DataSource.CreateCommand(sql);
        foreach (var value in values) command.Parameters.Add(new NpgsqlParameter { Value = value });
        await command.ExecuteNonQueryAsync(Token);
    }

    private static void AssertMetrics(RunMetrics expected, RunMetrics actual) =>
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));

    private sealed class MetricsClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(int seconds) => now += TimeSpan.FromSeconds(seconds);
    }
}

public sealed class RunMetricsDatabase : AgentDatabase
{
    protected override IReadOnlyList<string> Migrations =>
        ["0002_runs_events.sql", "0003_attempt_timers.sql", "0005_agents.sql", "0006_dispatch_outbox.sql",
            "0009_leases.sql", "0010_commands.sql", "0011_attempt_replacement.sql",
            "0015_team_type_registry.sql", "0016_role_bindings.sql"];
}
