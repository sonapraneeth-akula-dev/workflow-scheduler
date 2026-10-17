using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Wf.Contracts;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Checkpoints;
using Wf.Contracts.Definition;
using Wf.ControlPlane.Engine.Agents;
using Wf.ControlPlane.Engine.Checkpoints;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Sweeper;
using Wf.Engine.Core;
using Wf.Engine.Core.Placement;
using Wf.Engine.Core.StateMachine;
using Wf.Integration.Tests.Checkpoints;
using Wf.Integration.Tests.Reports;
using FixtureAgent = Wf.Integration.Tests.Checkpoints.CheckpointProducerTests.Agent;
using ReportHost = Wf.Integration.Tests.Reports.ReportPublishTests.ReportHost;
using static Wf.Integration.Tests.Checkpoints.CheckpointProducerTests;

namespace Wf.Integration.Tests.RemoteHttp;

public sealed class RemoteOperationTests(ReportPublishDatabase database) : IClassFixture<ReportPublishDatabase>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Immediate_or_202_pending_terminal_outcomes_preserve_outputs_and_occupy_the_agent_slot(
        bool immediate, bool failed)
    {
        await using var fixture = await Fixture.Create(database);
        fixture.Immediate = immediate;
        fixture.Failed = failed;
        var agent = await fixture.AddAgent();
        var command = await fixture.Start();
        if (!immediate)
        {
            await Until(() => Task.FromResult(fixture.Polls >= 3), agent.Work);
            Assert.Single(agent.Commands.Inflight);
            Assert.Equal(1, agent.Hosts.ProcessStartCount);
            Assert.Equal(ExecutionStatus.Running, await fixture.Status(command));
            var other = await fixture.Command("report");
            var blocked = await fixture.Place(other);
            Assert.Null(blocked.Agent);
            Assert.Single(fixture.Jobs);
            Assert.Equal(1L, await fixture.Scalar("""
                SELECT count(*) FROM run_events WHERE run_id=$1 AND
                    convert_from(payload,'UTF8')::jsonb->'Progress'->>'Phase'='3'
                """, Guid.Parse(command.Task.RunId)));
            fixture.Terminal = true;
        }
        await fixture.WaitForTerminal(command, agent);
        Assert.Equal(failed ? ExecutionStatus.Failed : ExecutionStatus.Succeeded, await fixture.Status(command));
        Assert.Equal(1, fixture.Posts);
        Assert.Single(fixture.Jobs);
        Assert.Equal(immediate ? 0 : 1, fixture.StatusReferences.Count);
        Assert.Equal(immediate ? 1L : 2L, await fixture.Scalar(
            "SELECT sequence FROM task_checkpoints WHERE run_id=$1", Guid.Parse(command.Task.RunId)));
        using var outputs = JsonDocument.Parse(Assert.IsType<string>(await fixture.Scalar(
            "SELECT outputs::text FROM task_attempts WHERE run_id=$1", Guid.Parse(command.Task.RunId))));
        Assert.Equal(failed ? 7 : 37, outputs.RootElement.GetProperty(failed ? "error" : "result").GetInt32());
        Assert.Equal(1, agent.Hosts.ProcessStartCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Abort_and_timeout_without_a_remote_cancel_contract_stop_locally_with_unknown_remote_outcome(bool timedOut)
    {
        await using var fixture = await Fixture.Create(database);
        var agent = await fixture.AddAgent();
        var command = await fixture.Command(cancel: false);
        command.DeadlineAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(timedOut ? 15 : 120));
        Assert.NotNull((await fixture.Place(command)).Agent);
        await Until(() => Task.FromResult(fixture.Polls > 0), agent.Work);
        if (!timedOut)
            await agent.Commands.HandleAsync(new() { Cancel = new() { Task = command.Task } }, Token);
        await fixture.WaitForTerminal(command, agent);
        Assert.Equal(timedOut ? ExecutionStatus.TimedOut : ExecutionStatus.Cancelled, await fixture.Status(command));
        Assert.Equal(0, fixture.Cancels);
        Assert.Single(fixture.Jobs);
        Assert.Equal(1, fixture.Posts);
        var run = Guid.Parse(command.Task.RunId);
        Assert.Equal(0L, await fixture.Scalar("""
            SELECT count(*) FROM run_events WHERE run_id=$1 AND
                convert_from(payload,'UTF8')::jsonb->'Progress'->>'Phase' IN ('6','7')
            """, run));
        Assert.Equal(1L, await fixture.Scalar("""
            SELECT count(*) FROM run_events WHERE run_id=$1 AND
                convert_from(payload,'UTF8')::jsonb->'Progress'->>'Phase'='8'
            """, run));
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("missing")]
    public async Task Expired_retention_or_missing_status_is_explicitly_unknown_and_never_submits_a_new_job(string mode)
    {
        await using var fixture = await Fixture.Create(database);
        fixture.MissingStatus = mode == "missing";
        var agent = await fixture.AddAgent();
        var command = await fixture.Start(retentionSeconds: mode == "expired" ? 1 : 3600);
        await fixture.WaitForTerminal(command, agent);
        Assert.Equal(ExecutionStatus.Failed, await fixture.Status(command));
        var error = Assert.IsType<string>(await fixture.Scalar(
            "SELECT error FROM task_attempts WHERE run_id=$1", Guid.Parse(command.Task.RunId)));
        Assert.Contains("RemoteOutcomeUnknown:", error);
        Assert.Equal(1, fixture.Posts);
        Assert.Single(fixture.Jobs);
        Assert.Equal(mode == "missing" ? 1 : 0, fixture.Polls);
    }

    [Theory]
    [InlineData("before-post", "lookup", false)]
    [InlineData("before-post", "dedup", true)]
    [InlineData("lost-response", "dedup", true)]
    [InlineData("lost-response", "lookup", true)]
    [InlineData("lost-response", "none", false)]
    [InlineData("submitted", "dedup", true)]
    [InlineData("submitted", "lookup", true)]
    public async Task Real_host_loss_or_lost_reply_resumes_only_the_durable_identity_without_duplicate_jobs(
        string loss, string contract, bool succeeds)
    {
        await using var fixture = await Fixture.Create(database);
        fixture.LoseResponse = loss == "lost-response";
        fixture.HoldSubmission = loss == "lost-response";
        var first = await fixture.AddAgent();
        Dispatch command;
        CheckpointSaved? pending = null;
        if (loss == "before-post")
        {
            await using var connection = await database.DataSource.OpenConnectionAsync(Token);
            await using var barrier = await connection.BeginTransactionAsync(Token);
            await using (var hold = new NpgsqlCommand("LOCK TABLE task_checkpoints IN ACCESS EXCLUSIVE MODE", connection))
                await hold.ExecuteNonQueryAsync(Token);
            try
            {
                command = await fixture.Start(contract);
                await Until(() => Task.FromResult(PendingCheckpoint(first.Outbox) is not null), first.Work);
                pending = PendingCheckpoint(first.Outbox)!.Clone();
                Assert.Equal(0, fixture.Posts);
                await KillHost(fixture.Marker);
                await first.Stop();
            }
            finally { await barrier.CommitAsync(Token); }
        }
        else
        {
            command = await fixture.Start(contract);
            if (loss == "lost-response")
                await fixture.Accepted.Task.WaitAsync(TimeSpan.FromSeconds(30), Token);
            else
            {
                await Until(async () => Equals(2L, await fixture.Scalar(
                    "SELECT sequence FROM task_checkpoints WHERE run_id=$1", Guid.Parse(command.Task.RunId))), first.Work);
                await Until(() => Task.FromResult(fixture.Polls > 0), first.Work);
            }
            await using var connection = await database.DataSource.OpenConnectionAsync(Token);
            await using var barrier = await connection.BeginTransactionAsync(Token);
            await using (var hold = new NpgsqlCommand("SELECT id FROM runs WHERE id=$1 FOR UPDATE", connection))
            {
                hold.Parameters.AddWithValue(Guid.Parse(command.Task.RunId));
                await hold.ExecuteScalarAsync(Token);
            }
            try
            {
                if (loss == "lost-response")
                {
                    fixture.ReleaseSubmission.TrySetResult();
                    await Until(() => Task.FromResult(first.Outbox.ContainsResult(command.Task)), first.Work);
                }
                else await KillHost(fixture.Marker);
                await first.Stop();
            }
            finally { await barrier.CommitAsync(Token); }
        }
        var run = Guid.Parse(command.Task.RunId);
        var firstSession = new AgentSession(fixture.AgentIds[0], fixture.Host.Registry.Find(fixture.AgentIds[0])!.SessionId);
        if (pending is not null)
        {
            // Replay the actual uploaded, journaled pointer after the interrupted gateway transaction.
            await new CheckpointApplier(database.DataSource, fixture.Host.Runs, fixture.Host.Blobs,
                NullLogger<CheckpointApplier>.Instance).AcceptAsync(firstSession, pending, Token);
        }
        fixture.LoseResponse = false;
        fixture.HoldSubmission = false;
        fixture.Terminal = true;
        var second = await fixture.AddAgent();
        using (var sweeper = new AgentLossSweeper(database.DataSource, fixture.Host.Registry, TimeProvider.System,
            NullLogger<AgentLossSweeper>.Instance))
        {
            var lost = await sweeper.ReconcileAsync(firstSession, [], Token);
            Assert.Single(lost);
            await using var mark = database.DataSource.CreateCommand("UPDATE agents SET status=2 WHERE id=$1");
            mark.Parameters.AddWithValue(firstSession.AgentId);
            await mark.ExecuteNonQueryAsync(Token);
            foreach (var item in lost)
                await fixture.Dispatcher.ReplaceLostAsync(item.RunId, item.TaskKey, item.Attempt, Token);
        }
        await fixture.WaitForTerminal(command, second);
        Assert.Equal(succeeds ? ExecutionStatus.Succeeded : ExecutionStatus.Failed, await fixture.Status(command));
        Assert.Equal(2L, await fixture.Scalar("SELECT count(*) FROM task_attempts WHERE run_id=$1", run));
        Assert.Equal(loss == "before-post" && !succeeds ? 0 : 1, fixture.Jobs.Count);
        Assert.All(fixture.Jobs.Values, submissions => Assert.Equal(1, submissions));
        Assert.Equal(loss == "lost-response" && contract == "dedup" ? 2 :
            loss == "before-post" && !succeeds ? 0 : 1, fixture.Posts);
        Assert.Equal(contract == "lookup" && loss != "submitted" ? 1 : 0, fixture.Lookups);
        Assert.True(fixture.StatusReferences.Count <= 1);
        Assert.Equal(1, first.Hosts.ProcessStartCount);
        Assert.Equal(1, second.Hosts.ProcessStartCount);
        if (!succeeds)
            Assert.Contains("RemoteOutcomeUnknown:", Assert.IsType<string>(await fixture.Scalar(
                "SELECT error FROM task_attempts WHERE run_id=$1 AND attempt=2", run)));
        if (loss == "lost-response" && contract == "dedup")
            Assert.Single(fixture.SubmissionKeys.Distinct(StringComparer.Ordinal));
        await Until(async () => await fixture.Scalar("""
            SELECT count(*) FROM task_attempts WHERE run_id=$1 AND attempt=2 AND ended_at IS NOT NULL
            """, run) is 1L, second.Work);
    }

    private static async Task KillHost(string marker)
    {
        var pid = int.Parse(await File.ReadAllTextAsync(marker, Token), System.Globalization.CultureInfo.InvariantCulture);
        using var process = Process.GetProcessById(pid);
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync(Token);
        Assert.True(process.HasExited);
    }

    private sealed class Fixture(ReportPublishDatabase database) : IAsyncDisposable
    {
        public ReportHost Host { get; private set; } = null!;
        public Dispatcher Dispatcher { get; private set; } = null!;
        public string Marker => Path.Combine(Host.Root, "remote-host-pid");
        public List<Guid> AgentIds { get; } = [];
        private readonly List<FixtureAgent> agents = [];
        public ConcurrentDictionary<string, int> Jobs { get; } = new(StringComparer.Ordinal);
        public ConcurrentDictionary<string, byte> StatusReferences { get; } = new(StringComparer.Ordinal);
        public ConcurrentQueue<string> SubmissionKeys { get; } = new();
        public TaskCompletionSource Accepted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSubmission { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Immediate { get; set; }
        public bool Failed { get; set; }
        public volatile bool Terminal;
        public volatile bool MissingStatus;
        public volatile bool LoseResponse;
        public volatile bool HoldSubmission;
        private int posts, polls, lookups, cancels;
        public int Posts => Volatile.Read(ref posts);
        public int Polls => Volatile.Read(ref polls);
        public int Lookups => Volatile.Read(ref lookups);
        public int Cancels => Volatile.Read(ref cancels);
        private string hash = "";

        public static async Task<Fixture> Create(ReportPublishDatabase database)
        {
            var fixture = new Fixture(database);
            var key = RandomNumberGenerator.GetBytes(32);
            fixture.Host = await ReportHost.Start(database.DataSource, key: key, configureServices: builder =>
            {
                builder.Services.AddSingleton<CheckpointApplier>();
                builder.Services.AddSingleton(new CheckpointUploadCapabilities(key, TimeProvider.System));
            }, configure: app =>
            {
                app.MapCheckpointUploadEndpoints();
                app.MapPost("/remote/jobs", (Func<HttpContext, Task<IResult>>)fixture.Submit);
                app.MapGet("/remote/status/one", (HttpContext context) =>
                {
                    Interlocked.Increment(ref fixture.polls);
                    fixture.StatusReferences.TryAdd(context.Request.Path, 0);
                    context.Response.Headers.RetryAfter = "0";
                    return fixture.MissingStatus ? Results.NotFound() : fixture.Response(fixture.Terminal);
                });
                app.MapGet("/remote/lookup/{submissionKey}", (string submissionKey) =>
                {
                    Interlocked.Increment(ref fixture.lookups);
                    return fixture.Jobs.ContainsKey(submissionKey) ? fixture.Response(true) : Results.NotFound();
                });
                app.MapPost("/remote/status/one/cancel", () =>
                {
                    Interlocked.Increment(ref fixture.cancels);
                    return Results.StatusCode(202);
                });
            });
            var archive = await ReportPublishTests.Package();
            fixture.Host.App.MapGet("/remote-fault-package.zip", () => Results.Bytes(archive, "application/zip"));
            fixture.hash = Convert.ToHexStringLower(SHA256.HashData(archive));
            fixture.Dispatcher = new(database.DataSource, fixture.Host.Runs, fixture.Host.Registry,
                new PlacementEngine(), TimeProvider.System);
            return fixture;
        }

        private async Task<IResult> Submit(HttpContext context)
        {
            Interlocked.Increment(ref posts);
            using var payload = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: Token);
            var run = payload.RootElement.GetProperty("runId").GetGuid();
            var task = payload.RootElement.GetProperty("taskKey").GetString();
            var correlation = "wf-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                run.ToString("D") + "\n" + task)));
            var key = context.Request.Headers["Idempotency-Key"].ToString();
            if (key.Length != 0) Assert.Equal(correlation, key);
            SubmissionKeys.Enqueue(key);
            // Lookup-only service derives its key from the caller's business correlation body.
            if (key.Length == 0) Jobs.AddOrUpdate(correlation, 1, (_, count) => count + 1);
            else Jobs.TryAdd(key, 1);
            Accepted.TrySetResult();
            if (HoldSubmission) await ReleaseSubmission.Task.WaitAsync(Token);
            if (LoseResponse) { context.Abort(); return Results.Empty; }
            if (Immediate) return Response(true);
            context.Response.Headers.Location = "/remote/status/one";
            context.Response.Headers.RetryAfter = "0";
            return Results.Text("""{"state":"Pending"}""", "application/json", statusCode: 202);
        }

        private IResult Response(bool terminal) => Results.Text(!terminal ? """{"state":"Running"}""" :
            Failed ? """{"state":"Failed","error":7}""" : """{"state":"Succeeded","value":37}""", "application/json");

        public async Task<FixtureAgent> AddAgent()
        {
            var id = Guid.NewGuid();
            var agent = new FixtureAgent(Host, id);
            agents.Add(agent);
            AgentIds.Add(id);
            await Until(() => Task.FromResult(Host.Registry.Find(id) is not null), agent.Work);
            return agent;
        }

        public async Task<Dispatch> Command(string task = "remoteOperation", string contract = "dedup", int retentionSeconds = 3600,
            bool cancel = true)
        {
            var attempt = await Host.Seed(dispatch: false, task: task);
            return new()
            {
                Task = new() { RunId = attempt.RunId.ToString("D"), NodeKey = "$post", TaskKey = task, Attempt = 1 },
                Version = "1.0.0", Sha256 = hash, PackageUri = new Uri(Host.Address, "/remote-fault-package.zip").AbsoluteUri,
                VariablesJson = ByteString.CopyFromUtf8(JsonSerializer.Serialize(new
                {
                    remoteUrl = new Uri(Host.Address, "/remote/jobs").AbsoluteUri,
                    remoteContract = contract, remoteRetentionSeconds = retentionSeconds, remoteCancel = cancel ? 1 : 0,
                    checkpointMarker = Marker
                })),
                DeadlineAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(2))
            };
        }

        public async Task<Dispatch> Start(string contract = "dedup", int retentionSeconds = 3600)
        {
            var command = await Command(contract: contract, retentionSeconds: retentionSeconds);
            Assert.NotNull((await Place(command)).Agent);
            return command;
        }

        public async Task<PlacementDecision> Place(Dispatch command) => await Dispatcher.DispatchAsync(
            await Host.Runs.LoadAsync(Guid.Parse(command.Task.RunId), Token),
            new(Guid.Parse(command.Task.RunId), "$post", command.Task.TaskKey, checked((int)command.Task.Attempt), hash,
                new(new(pool: Host.Pool))), command, Token);

        public async Task<ExecutionStatus> Status(Dispatch command) =>
            (await Host.Runs.LoadAsync(Guid.Parse(command.Task.RunId), Token)).Snapshot.Tasks["$post/" + command.Task.TaskKey];

        public async Task WaitForTerminal(Dispatch command, FixtureAgent agent)
        {
            await Until(async () => TransitionTable.IsTerminal(await Status(command)), agent.Work);
            await Until(() => Task.FromResult(agent.Commands.Inflight.Count == 0), agent.Work);
        }

        public async Task<object?> Scalar(string sql, Guid run)
        {
            await using var command = database.DataSource.CreateCommand(sql);
            command.Parameters.AddWithValue(run);
            return await command.ExecuteScalarAsync(Token);
        }

        public async ValueTask DisposeAsync()
        {
            ReleaseSubmission.TrySetResult();
            for (var index = agents.Count - 1; index >= 0; index--) await agents[index].DisposeAsync();
            Dispatcher.Dispose();
            await Host.DisposeAsync();
        }
    }
}
