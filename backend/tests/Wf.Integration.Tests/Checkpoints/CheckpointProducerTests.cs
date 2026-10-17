using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Wf.Agent;
using Wf.Agent.Hosts;
using Wf.Agent.Journal;
using Wf.Agent.Packages;
using Wf.Contracts;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Checkpoints;
using Wf.ControlPlane.Engine.Agents;
using Wf.ControlPlane.Engine.Checkpoints;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Sweeper;
using Wf.Engine.Core;
using Wf.Engine.Core.Placement;
using Wf.Integration.Tests.Reports;
using ReportHost = Wf.Integration.Tests.Reports.ReportPublishTests.ReportHost;

namespace Wf.Integration.Tests.Checkpoints;

public sealed class CheckpointProducerTests(ReportPublishDatabase database) : IClassFixture<ReportPublishDatabase>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

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

    private static CheckpointSaved? PendingCheckpoint(AgentOutbox outbox)
    {
        ulong sequence = 0;
        while (outbox.NextAfter(sequence) is { } message)
        {
            sequence = message.Sequence;
            if (message.CheckpointSaved is not null) return message.CheckpointSaved;
        }
        return null;
    }

    private static async Task Until(Func<Task<bool>> condition, Task connection)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        while (!await condition())
        {
            if (connection.IsCompleted) await connection;
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class Agent : IAsyncDisposable
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

        public Agent(ReportHost host, Guid id)
        {
            Journal = Path.Combine(host.Root, "checkpoint-journal-" + id.ToString("N"));
            var options = new AgentOptions([host.GatewayAddress], id.ToString("D"), "checkpoint-" + id.ToString("N"),
                host.Pool, 1, heartbeatInterval: TimeSpan.FromMilliseconds(50), resultJournalDirectory: Journal);
            packages = new(Path.Combine(host.Root, "checkpoint-packages-" + id.ToString("N")),
                NullLogger<PackageCache>.Instance);
            Outbox = new(new ResultJournal(Journal, id.ToString("D")));
            Hosts = new(NullLogger<TaskHostPool>.Instance, options: options);
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
}
