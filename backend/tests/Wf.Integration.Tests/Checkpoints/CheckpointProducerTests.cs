using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Wf.Agent;
using Wf.Agent.Hosts;
using Wf.Agent.Journal;
using Wf.Agent.Packages;
using Wf.Agent.Secrets;
using Wf.Contracts;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Checkpoints;
using Wf.Contracts.Definition;
using Wf.Contracts.Progress;
using Wf.ControlPlane.Engine.Agents;
using Wf.ControlPlane.Engine.Checkpoints;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Sweeper;
using Wf.Engine.Core;
using Wf.Engine.Core.Placement;
using Wf.Engine.Core.StateMachine;
using Wf.Integration.Tests.Reports;
using ReportHost = Wf.Integration.Tests.Reports.ReportPublishTests.ReportHost;

namespace Wf.Integration.Tests.Checkpoints;

public sealed class CheckpointProducerTests(ReportPublishDatabase database) : IClassFixture<ReportPublishDatabase>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RemoteOperation_actual_SDK_waits_for_durable_engine_ACK_before_POST_then_polls_one_remote_job()
    {
        var posts = 0;
        var polls = 0;
        string? submissionKey = null;
        var key = RandomNumberGenerator.GetBytes(32);
        await using var host = await ReportHost.Start(database.DataSource, key: key, configureServices: builder =>
        {
            builder.Services.AddSingleton<CheckpointApplier>();
            builder.Services.AddSingleton(new CheckpointUploadCapabilities(key, TimeProvider.System));
        }, configure: app =>
        {
            app.MapCheckpointUploadEndpoints();
            app.MapPost("/remote/jobs", (HttpContext context) =>
            {
                Interlocked.Increment(ref posts);
                submissionKey = context.Request.Headers["Idempotency-Key"].ToString();
                context.Response.Headers.Location = "/remote/status/one";
                context.Response.Headers.RetryAfter = "1";
                return Results.Text("""{"state":"Pending"}""", "application/json", statusCode: 202);
            });
            app.MapGet("/remote/status/one", (HttpContext context) =>
            {
                context.Response.Headers.RetryAfter = "1";
                return Results.Text(Interlocked.Increment(ref polls) == 1
                    ? """{"state":"Running"}""" : """{"state":"Succeeded","value":31}""", "application/json");
            });
        });
        var archive = await ReportPublishTests.Package();
        host.App.MapGet("/remote-checkpoint-package.zip", () => Results.Bytes(archive, "application/zip"));
        var hash = Convert.ToHexStringLower(SHA256.HashData(archive));
        var id = Guid.NewGuid();
        await using var agent = new Agent(host, id);
        await Until(() => Task.FromResult(host.Registry.Find(id) is not null), agent.Work);
        var attempt = await host.Seed(dispatch: false, task: "remoteOperation");
        var command = new Dispatch
        {
            Task = new() { RunId = attempt.RunId.ToString("D"), NodeKey = "$post", TaskKey = "remoteOperation", Attempt = 1 },
            Version = "1.0.0", Sha256 = hash, PackageUri = new Uri(host.Address, "/remote-checkpoint-package.zip").AbsoluteUri,
            VariablesJson = ByteString.CopyFromUtf8(JsonSerializer.Serialize(new
            {
                remoteUrl = new Uri(host.Address, "/remote/jobs").AbsoluteUri
            })),
            DeadlineAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(2))
        };
        using var dispatcher = new Dispatcher(database.DataSource, host.Runs, host.Registry,
            new PlacementEngine(), TimeProvider.System);
        await using var indexLock = await database.DataSource.OpenConnectionAsync(Token);
        await using var barrier = await indexLock.BeginTransactionAsync(Token);
        await using (var hold = new NpgsqlCommand("LOCK TABLE task_checkpoints IN ACCESS EXCLUSIVE MODE", indexLock))
            await hold.ExecuteNonQueryAsync(Token);
        try
        {
            var placed = await dispatcher.DispatchAsync(await host.Runs.LoadAsync(attempt.RunId, Token),
                new(attempt.RunId, "$post", "remoteOperation", 1, hash, new(new(pool: host.Pool))), command, Token);
            Assert.Equal(id, placed.Agent!.Id);
            await Until(async () =>
            {
                var error = await Scalar("SELECT error FROM task_attempts WHERE run_id=$1 AND error IS NOT NULL", attempt.RunId);
                Assert.True(error is null or DBNull, $"SDK remote operation failed: {error}");
                return PendingCheckpoint(agent.Outbox) is not null;
            }, agent.Work);
            Assert.Equal(0, Volatile.Read(ref posts));
            Assert.Equal(0, Volatile.Read(ref polls));
            Assert.Single(agent.Commands.Inflight);
            var pending = PendingCheckpoint(agent.Outbox)!;
            using var bytes = new MemoryStream();
            await host.Blobs.DownloadAsync(new(Wf.ControlPlane.Storage.BlobKind.Checkpoints, pending.Sha256,
                (long)pending.SizeBytes, pending.BlobKey), bytes, Token);
            using var state = JsonDocument.Parse(bytes.ToArray());
            Assert.Equal("SubmissionPending", state.RootElement.GetProperty("Stage").GetString());
            Assert.Equal("wf-" + Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
                attempt.RunId.ToString("D") + "\n$post/remoteOperation"))),
                state.RootElement.GetProperty("SubmissionKey").GetString());
        }
        finally { await barrier.CommitAsync(Token); }
        await Until(async () => TransitionTable.IsTerminal(
            (await host.Runs.LoadAsync(attempt.RunId, Token)).Snapshot.Tasks[attempt.TaskKey]), agent.Work);
        Assert.Equal(ExecutionStatus.Succeeded,
            (await host.Runs.LoadAsync(attempt.RunId, Token)).Snapshot.Tasks[attempt.TaskKey]);
        Assert.Equal(1, posts);
        Assert.Equal(2, polls);
        Assert.StartsWith("wf-", submissionKey);
        Assert.Equal(2L, await Scalar("SELECT sequence FROM task_checkpoints WHERE run_id=$1", attempt.RunId));
        using var outputs = JsonDocument.Parse(Assert.IsType<string>(await Scalar(
            "SELECT outputs::text FROM task_attempts WHERE run_id=$1", attempt.RunId)));
        Assert.Equal(31, outputs.RootElement.GetProperty("result").GetInt32());
        await Until(() => Task.FromResult(agent.Commands.Inflight.Count == 0), agent.Work);
        Assert.Null(PendingCheckpoint(agent.Outbox));
        Assert.Equal(1, agent.Hosts.ProcessStartCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RemoteOperation_cancellation_survives_blocked_engine_and_persists_only_confirmed_outcome(bool timedOut, bool confirmed)
    {
        var posts = 0;
        var cancellations = 0;
        var polls = 0;
        var key = RandomNumberGenerator.GetBytes(32);
        await using var host = await ReportHost.Start(database.DataSource, key: key, configureServices: builder =>
        {
            builder.Services.AddSingleton<CheckpointApplier>();
            builder.Services.AddSingleton(new CheckpointUploadCapabilities(key, TimeProvider.System));
        }, configure: app =>
        {
            app.MapCheckpointUploadEndpoints();
            app.MapPost("/remote/jobs", (HttpContext context) =>
            {
                Interlocked.Increment(ref posts);
                context.Response.Headers.Location = "/remote/status/one";
                context.Response.Headers.RetryAfter = "3600";
                return Results.Text("""{"state":"Pending"}""", "application/json", statusCode: 202);
            });
            app.MapPost("/remote/status/one/cancel", (HttpContext context) =>
            {
                Interlocked.Increment(ref cancellations);
                Assert.Equal(0, context.Request.ContentLength ?? 0);
                Assert.False(context.Request.Headers.ContainsKey("Idempotency-Key"));
                context.Response.Headers.RetryAfter = confirmed ? "0" : "3600";
                return Results.StatusCode(202);
            });
            app.MapGet("/remote/status/one", () =>
            {
                Interlocked.Increment(ref polls);
                return Results.Text(confirmed ? """{"state":"Cancelled"}""" : """{"state":"Running"}""",
                    "application/json");
            });
        });
        var archive = await ReportPublishTests.Package();
        host.App.MapGet("/remote-cancellation-package.zip", () => Results.Bytes(archive, "application/zip"));
        var hash = Convert.ToHexStringLower(SHA256.HashData(archive));
        var id = Guid.NewGuid();
        await using var agent = new Agent(host, id);
        await Until(() => Task.FromResult(host.Registry.Find(id) is not null), agent.Work);
        var attempt = await host.Seed(dispatch: false, task: "remoteOperation");
        var command = new Dispatch
        {
            Task = new() { RunId = attempt.RunId.ToString("D"), NodeKey = "$post", TaskKey = "remoteOperation", Attempt = 1 },
            Version = "1.0.0", Sha256 = hash,
            PackageUri = new Uri(host.Address, "/remote-cancellation-package.zip").AbsoluteUri,
            VariablesJson = ByteString.CopyFromUtf8(JsonSerializer.Serialize(new
            {
                remoteUrl = new Uri(host.Address, "/remote/jobs").AbsoluteUri
            })),
            DeadlineAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(timedOut ? 15 : 120))
        };
        using var dispatcher = new Dispatcher(database.DataSource, host.Runs, host.Registry,
            new PlacementEngine(), TimeProvider.System);
        var placed = await dispatcher.DispatchAsync(await host.Runs.LoadAsync(attempt.RunId, Token),
            new(attempt.RunId, "$post", "remoteOperation", 1, hash, new(new(pool: host.Pool))), command, Token);
        Assert.Equal(id, placed.Agent!.Id);
        await Until(async () => Equals(3L, await Scalar("""
            SELECT count(*) FROM run_events WHERE run_id=$1 AND convert_from(payload,'UTF8')::jsonb ? 'Progress'
            """, attempt.RunId)), agent.Work);
        await using var connection = await database.DataSource.OpenConnectionAsync(Token);
        await using var barrier = await connection.BeginTransactionAsync(Token);
        await using (var hold = new NpgsqlCommand("SELECT 1 FROM runs WHERE id=$1 FOR UPDATE", connection))
        {
            hold.Parameters.AddWithValue(attempt.RunId);
            await hold.ExecuteScalarAsync(Token);
        }
        var outcome = timedOut ? TaskOutcome.TimedOut : TaskOutcome.Cancelled;
        var finalPhase = confirmed ? RemoteOperationPhase.CancelConfirmed : RemoteOperationPhase.OutcomeUnknown;
        try
        {
            if (!timedOut)
                await agent.Commands.HandleAsync(new() { Cancel = new() { Task = command.Task } }, Token);
            await Until(() => Task.FromResult(agent.Outbox.ContainsResult(command.Task)), agent.Work);
            await Until(() => Task.FromResult(agent.Commands.Inflight.Count == 0), agent.Work);
            var pending = PendingMessages(agent.Outbox);
            Assert.Equal([RemoteOperationPhase.CancelRequested, finalPhase],
                pending.Where(message => message.TaskProgress is not null).Select(message => message.TaskProgress.Phase));
            Assert.Equal(outcome, Assert.Single(pending, message => message.TaskCompleted is not null).TaskCompleted.Outcome);
            Assert.Equal(1, posts);
            Assert.Equal(1, cancellations);
            Assert.Equal(confirmed ? 1 : 0, polls);
            Assert.Contains(command.Task, agent.Outbox.UnacknowledgedAttempts());
            var copy = Path.Combine(host.Root, "progress-journal-copy");
            Directory.CreateDirectory(copy);
            File.Copy(Path.Combine(agent.Journal, "results.wfj"), Path.Combine(copy, "results.wfj"));
            using var restored = new AgentOutbox(new ResultJournal(copy, id.ToString("D")));
            Assert.Equal(2, PendingMessages(restored).Count(message => message.TaskProgress is not null));
        }
        finally { await barrier.CommitAsync(Token); }
        await Until(async () => TransitionTable.IsTerminal(
            (await host.Runs.LoadAsync(attempt.RunId, Token)).Snapshot.Tasks[attempt.TaskKey]), agent.Work);
        Assert.Equal(timedOut ? ExecutionStatus.TimedOut : ExecutionStatus.Cancelled,
            (await host.Runs.LoadAsync(attempt.RunId, Token)).Snapshot.Tasks[attempt.TaskKey]);
        await using (var events = database.DataSource.CreateCommand("""
            SELECT payload FROM run_events WHERE run_id=$1 AND convert_from(payload,'UTF8')::jsonb ? 'Progress' ORDER BY seq
            """))
        {
            events.Parameters.AddWithValue(attempt.RunId);
            await using var rows = await events.ExecuteReaderAsync(Token);
            var progress = new List<RemoteOperationProgress>();
            while (await rows.ReadAsync(Token))
            {
                var text = System.Text.Encoding.UTF8.GetString(rows.GetFieldValue<byte[]>(0));
                Assert.DoesNotContain(host.Address.Authority, text);
                Assert.DoesNotContain("remote/jobs", text);
                using var json = JsonDocument.Parse(text);
                progress.Add(JsonSerializer.Deserialize<RemoteOperationProgress>(json.RootElement.GetProperty("Progress"))!);
            }
            Assert.Equal([1U, 2U, 3U, 4U, 5U], progress.Select(item => item.Sequence));
            Assert.Equal([RemoteOperationPhase.SubmissionPending, RemoteOperationPhase.Submitted,
                RemoteOperationPhase.Waiting, RemoteOperationPhase.CancelRequested, finalPhase], progress.Select(item => item.Phase));
        }
        Assert.Equal(1, agent.Hosts.ProcessStartCount);
    }

    private static List<AgentMessage> PendingMessages(AgentOutbox outbox)
    {
        var messages = new List<AgentMessage>();
        ulong sequence = 0;
        while (outbox.NextAfter(sequence) is { } message)
        {
            messages.Add(message);
            sequence = message.Sequence;
        }
        return messages;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Actual_SDK_checkpoint_secret_guard_prevents_upload_journal_and_index_but_allows_safe_state(int mode)
    {
        var value = "checkpoint-runtime-canary-\"" + Guid.NewGuid().ToString("N");
        var uploads = 0;
        var key = RandomNumberGenerator.GetBytes(32);
        await using var host = await ReportHost.Start(database.DataSource, key: key, configureServices: builder =>
        {
            builder.Services.AddSingleton<CheckpointApplier>();
            builder.Services.AddSingleton(new CheckpointUploadCapabilities(key, TimeProvider.System));
        }, configure: app =>
        {
            app.Use(async (context, next) =>
            {
                if (context.Request.Path == CheckpointProtocol.EndpointPath) Interlocked.Increment(ref uploads);
                await next(context);
            });
            app.MapCheckpointUploadEndpoints();
        });
        var archive = await ReportPublishTests.Package();
        host.App.MapGet("/secret-checkpoint-package.zip", () => Microsoft.AspNetCore.Http.Results.Bytes(archive, "application/zip"));
        var hash = Convert.ToHexStringLower(SHA256.HashData(archive));
        var id = Guid.NewGuid();
        await using var agent = new Agent(host, id, new CanarySecretStore(value));
        await Until(() => Task.FromResult(host.Registry.Find(id) is not null), agent.Work);
        var attempt = await host.Seed(dispatch: false, task: "checkpointSecret");
        var command = new Dispatch
        {
            Task = new() { RunId = attempt.RunId.ToString("D"), NodeKey = "$post", TaskKey = "checkpointSecret", Attempt = 1 },
            Version = "1.0.0", Sha256 = hash, PackageUri = new Uri(host.Address, "/secret-checkpoint-package.zip").AbsoluteUri,
            VariablesJson = ByteString.CopyFromUtf8(JsonSerializer.Serialize(new
            {
                amount = mode, credential = new { Name = "secret://local/checkpoint-canary" }
            })),
            DeadlineAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(2))
        };
        using var dispatcher = new Dispatcher(database.DataSource, host.Runs, host.Registry,
            new PlacementEngine(), TimeProvider.System);
        var placed = await dispatcher.DispatchAsync(await host.Runs.LoadAsync(attempt.RunId, Token),
            new(attempt.RunId, "$post", "checkpointSecret", 1, hash, new(new(pool: host.Pool))), command, Token);
        Assert.Equal(id, placed.Agent!.Id);
        await Until(async () => TransitionTable.IsTerminal(
            (await host.Runs.LoadAsync(attempt.RunId, Token)).Snapshot.Tasks[attempt.TaskKey]), agent.Work);
        var run = await host.Runs.LoadAsync(attempt.RunId, Token);
        Assert.Equal(mode == 3 ? ExecutionStatus.Succeeded : ExecutionStatus.Failed, run.Snapshot.Tasks[attempt.TaskKey]);
        Assert.Equal(mode == 3 ? 1 : 0, uploads);
        Assert.Equal(mode == 3 ? 1L : 0L,
            await Scalar("SELECT count(*) FROM task_checkpoints WHERE run_id=$1", attempt.RunId));
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM task_attempts WHERE run_id=$1", attempt.RunId));
        if (mode != 3)
        {
            Assert.Equal(FailureCategory.ValidationError, run.Snapshot.Metrics!.Tasks[attempt.TaskKey].Attempts.Single().FailureCategory);
            var error = Assert.IsType<string>(await Scalar("SELECT error FROM task_attempts WHERE run_id=$1", attempt.RunId));
            Assert.Contains("Checkpoint state contains a resolved secret value.", error);
            Assert.DoesNotContain(value, error);
            Assert.DoesNotContain(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value)), error);
            Assert.False(Directory.Exists(Path.Combine(host.Root, "checkpoints", attempt.RunId.ToString("D"))));
        }
        await Until(() => Task.FromResult(agent.Commands.Inflight.Count == 0), agent.Work);
        Assert.Null(PendingCheckpoint(agent.Outbox));
        await agent.Stop();
        foreach (var path in Directory.GetFiles(agent.Journal, "*", SearchOption.AllDirectories))
        {
            var text = System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path, Token));
            Assert.DoesNotContain(value, text);
            Assert.DoesNotContain(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value)), text);
        }
        Assert.Equal(1, agent.Hosts.ProcessStartCount);
    }

    [Fact]
    public async Task Checkpoint_actual_SDK_blob_journal_engine_ack_and_replacement_resume_preserve_one_MiB_and_sequence()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        await using var host = await ReportHost.Start(database.DataSource, key: key, configureServices: builder =>
        {
            builder.Services.AddSingleton<CheckpointApplier>();
            builder.Services.AddSingleton(new CheckpointUploadCapabilities(key, TimeProvider.System));
        }, configure: app => app.MapCheckpointUploadEndpoints());
        var archive = await ReportPublishTests.Package();
        host.App.MapGet("/checkpoint-package.zip", () => Microsoft.AspNetCore.Http.Results.Bytes(archive, "application/zip"));
        var hash = Convert.ToHexStringLower(SHA256.HashData(archive));
        var marker = Path.Combine(host.Root, "checkpoint-user-code-marker");
        var firstId = Guid.NewGuid();
        await using var first = new Agent(host, firstId);
        await Until(() => Task.FromResult(host.Registry.Find(firstId) is not null), first.Work);
        var attempt = await host.Seed(dispatch: false, task: "checkpoint");
        var reference = new AttemptRef
        {
            RunId = attempt.RunId.ToString("D"), NodeKey = "$post", TaskKey = "checkpoint", Attempt = 1
        };
        var command = new Dispatch
        {
            Task = reference, Version = "1.0.0", Sha256 = hash,
            PackageUri = new Uri(host.Address, "/checkpoint-package.zip").AbsoluteUri,
            VariablesJson = ByteString.CopyFromUtf8(JsonSerializer.Serialize(new { checkpointMarker = marker })),
            DeadlineAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(2))
        };
        using var dispatcher = new Dispatcher(database.DataSource, host.Runs, host.Registry,
            new PlacementEngine(), TimeProvider.System);
        await using var indexLock = await database.DataSource.OpenConnectionAsync(Token);
        await using var barrier = await indexLock.BeginTransactionAsync(Token);
        await using (var hold = new NpgsqlCommand("LOCK TABLE task_checkpoints IN ACCESS EXCLUSIVE MODE", indexLock))
            await hold.ExecuteNonQueryAsync(Token);
        CheckpointSaved saved;
        try
        {
            var placed = await dispatcher.DispatchAsync(await host.Runs.LoadAsync(attempt.RunId, Token),
                new(attempt.RunId, "$post", "checkpoint", 1, hash, new(new(pool: host.Pool))), command, Token);
            Assert.Equal(firstId, placed.Agent!.Id);
            await Until(async () =>
            {
                var error = await Scalar("SELECT error FROM task_attempts WHERE run_id=$1 AND error IS NOT NULL", attempt.RunId);
                Assert.True(error is null or DBNull, $"SDK checkpoint attempt failed: {error}");
                return PendingCheckpoint(first.Outbox) is not null;
            }, first.Work);
            saved = PendingCheckpoint(first.Outbox)!;
            Assert.Equal((ulong)CheckpointProtocol.MaximumBytes, saved.SizeBytes);
            Assert.Equal(1UL, saved.CheckpointSequence);
            Assert.Equal(DBNull.Value, await Scalar(
                "SELECT resumed_from_checkpoint_at FROM task_attempts WHERE run_id=$1 AND attempt=1", attempt.RunId));
            Assert.True(File.Exists(Path.Combine([host.Root, .. saved.BlobKey.Split('/')])));
            Assert.Single(first.Commands.Inflight);
            Assert.False(File.Exists(marker));
            Assert.False(first.Outbox.ContainsResult(reference));
            Assert.Equal(ExecutionStatus.Running,
                (await host.Runs.LoadAsync(attempt.RunId, Token)).Snapshot.Tasks[attempt.TaskKey]);
            using var state = new MemoryStream();
            await host.Blobs.DownloadAsync(new(Wf.ControlPlane.Storage.BlobKind.Checkpoints, saved.Sha256,
                (long)saved.SizeBytes, saved.BlobKey), state, Token);
            Assert.Equal(CheckpointProtocol.MaximumBytes, state.Length);
        }
        finally { await barrier.CommitAsync(Token); }
        await Until(async () => await Scalar("SELECT sequence FROM task_checkpoints WHERE run_id=$1", attempt.RunId) is 1L &&
            PendingCheckpoint(first.Outbox) is null && File.Exists(marker), first.Work);
        Assert.Equal("1", await File.ReadAllTextAsync(marker, Token));
        var firstSession = new AgentSession(firstId, host.Registry.Find(firstId)!.SessionId);
        await first.Stop();
        Assert.Equal(1, first.Hosts.ProcessStartCount);
        var nextId = Guid.NewGuid();
        await using var next = new Agent(host, nextId);
        await Until(() => Task.FromResult(host.Registry.Find(nextId) is not null), next.Work);
        using (var losses = new AgentLossSweeper(database.DataSource, host.Registry, TimeProvider.System,
            NullLogger<AgentLossSweeper>.Instance))
        {
            var lost = await losses.ReconcileAsync(firstSession, [], Token);
            Assert.Single(lost);
            await using (var markLost = database.DataSource.CreateCommand("UPDATE agents SET status=2 WHERE id=$1"))
            {
                markLost.Parameters.AddWithValue(firstId);
                await markLost.ExecuteNonQueryAsync(Token);
            }
            foreach (var item in lost)
                await dispatcher.ReplaceLostAsync(item.RunId, item.TaskKey, item.Attempt, Token);
        }
        await Until(async () => (await host.Runs.LoadAsync(attempt.RunId, Token)).Snapshot.Tasks[attempt.TaskKey] ==
            ExecutionStatus.Succeeded, next.Work);
        Assert.Equal(2L, await Scalar("SELECT sequence FROM task_checkpoints WHERE run_id=$1", attempt.RunId));
        Assert.Equal((short)2, await Scalar("SELECT attempt FROM task_checkpoints WHERE run_id=$1", attempt.RunId));
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM task_checkpoints WHERE run_id=$1", attempt.RunId));
        Assert.Equal(2L, await Scalar("SELECT count(*) FROM task_attempts WHERE run_id=$1", attempt.RunId));
        var at = new DateTimeOffset(Assert.IsType<DateTime>(
            await Scalar("SELECT saved_at FROM task_checkpoints WHERE run_id=$1", attempt.RunId)));
        Assert.True(at - saved.SavedAt.ToDateTimeOffset() >= CheckpointProtocol.MinimumInterval);
        using var outputDocument = JsonDocument.Parse((string)(await Scalar(
            "SELECT outputs::text FROM task_attempts WHERE run_id=$1 AND attempt=2", attempt.RunId))!);
        var outputs = outputDocument.RootElement;
        Assert.Equal(2, outputs.GetProperty("attempt").GetInt32());
        Assert.Equal(CheckpointProtocol.MaximumBytes - 2, outputs.GetProperty("loadedLength").GetInt32());
        Assert.Equal(saved.SavedAt.ToDateTimeOffset(), DateTimeOffset.Parse(outputs.GetProperty("resumedFrom").GetString()!,
            System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(saved.SavedAt.ToDateTimeOffset(), new DateTimeOffset(Assert.IsType<DateTime>(await Scalar(
            "SELECT resumed_from_checkpoint_at FROM task_attempts WHERE run_id=$1 AND attempt=2", attempt.RunId))));
        Assert.Equal("continued", outputs.GetProperty("loadedAfterSave").GetString());
        Assert.Equal("2", await File.ReadAllTextAsync(marker, Token));
        await Until(() => Task.FromResult(next.Commands.Inflight.Count == 0), next.Work);
        await next.Stop();
        using var restored = new AgentOutbox(new ResultJournal(next.Journal, nextId.ToString("D")));
        Assert.Null(PendingCheckpoint(restored));
        Assert.Equal(1, next.Hosts.ProcessStartCount);
    }

    private async Task<object?> Scalar(string sql, Guid run)
    {
        await using var command = database.DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(run);
        return await command.ExecuteScalarAsync(Token);
    }

    internal static CheckpointSaved? PendingCheckpoint(AgentOutbox outbox)
    {
        ulong sequence = 0;
        while (outbox.NextAfter(sequence) is { } message)
        {
            sequence = message.Sequence;
            if (message.CheckpointSaved is not null) return message.CheckpointSaved;
        }
        return null;
    }

    internal static async Task Until(Func<Task<bool>> condition, Task connection)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        while (!await condition())
        {
            if (connection.IsCompleted) await connection;
            await Task.Delay(10, timeout.Token);
        }
    }

    internal sealed class Agent : IAsyncDisposable
    {
        private readonly CancellationTokenSource stopping = CancellationTokenSource.CreateLinkedTokenSource(Token);
        private readonly PackageCache packages;
        private readonly AgentConnection connection;
        public AgentOutbox Outbox { get; }
        public TaskCommands Commands { get; }
        public TaskHostPool Hosts { get; }
        public Task Work { get; }
        public string Journal { get; }
        private bool stopped;

        public Agent(ReportHost host, Guid id, ISecretStore? secrets = null)
        {
            Journal = Path.Combine(host.Root, "checkpoint-journal-" + id.ToString("N"));
            var options = new AgentOptions([host.GatewayAddress], id.ToString("D"), "checkpoint-" + id.ToString("N"),
                host.Pool, 1, heartbeatInterval: TimeSpan.FromMilliseconds(50), resultJournalDirectory: Journal);
            packages = new(Path.Combine(host.Root, "checkpoint-packages-" + id.ToString("N")),
                NullLogger<PackageCache>.Instance);
            Outbox = new(new ResultJournal(Journal, id.ToString("D")));
            Hosts = new(NullLogger<TaskHostPool>.Instance, options: options, secretStore: secrets);
            Commands = new(options, packages, Hosts, Outbox, TimeProvider.System, NullLogger<TaskCommands>.Instance);
            connection = new(options, new AgentTelemetry(TimeProvider.System, Commands), Commands, TimeProvider.System,
                NullLogger<AgentConnection>.Instance, Outbox);
            Work = connection.RunAsync(stopping.Token);
        }

        public async Task Stop()
        {
            if (stopped) return;
            stopped = true;
            try
            {
                await stopping.CancelAsync();
                await Work.WaitAsync(TimeSpan.FromSeconds(10), Token);
            }
            finally
            {
                try { await Commands.DisposeAsync(); }
                finally
                {
                    try { await Hosts.DisposeAsync(); }
                    finally
                    {
                        connection.Dispose();
                        packages.Dispose();
                        Outbox.Dispose();
                    }

                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Stop();
            stopping.Dispose();
        }
    }

    private sealed class CanarySecretStore(string value) : ISecretStore
    {
        public ValueTask<string> ResolveAsync(SecretReference reference, CancellationToken cancellationToken = default)
        {
            Assert.Equal("secret://local/checkpoint-canary", reference.Name);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(value);
        }
    }
}
