using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;
using Wf.Contracts;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Checkpoints;
using Wf.Contracts.Events;
using Wf.ControlPlane.Engine.Agents;
using Wf.ControlPlane.Engine.Checkpoints;
using Wf.ControlPlane.Engine.Maintenance;
using Wf.ControlPlane.Engine.Reservations;
using Wf.ControlPlane.Storage;
using Wf.Integration.Tests.Execution;
using Runtime = Wf.Integration.Tests.Execution.HostedRunExecutionTests.Runtime;

namespace Wf.Integration.Tests.Checkpoints;

public sealed partial class CheckpointApplierTests(HostedExecutionDatabase database) : IClassFixture<HostedExecutionDatabase>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Highest_sequence_wins_and_exact_replays_do_not_regress_or_duplicate_the_logical_task()
    {
        await using var fixture = await Fixture.Create(database);
        var first = await fixture.Upload("""{"position":1}""");
        await fixture.Accept(first);
        fixture.Runtime.Clock.Now = fixture.Runtime.Clock.Now.AddSeconds(10);
        var last = await fixture.Upload("""{"position":3}""", 3);
        await new CheckpointApplier(database.DataSource, fixture.Runtime.Store, fixture.Blobs,
            NullLogger<CheckpointApplier>.Instance).AcceptAsync(fixture.Runtime.Session, last, Token);
        var middle = await fixture.Upload("""{"position":2}""", 2);
        await fixture.Accept(middle);
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => fixture.Accept(last)));
        Assert.Equal(3L, await fixture.Runtime.Scalar("SELECT sequence FROM task_checkpoints WHERE run_id=$1"));
        Assert.Equal(last.BlobKey, await fixture.Runtime.Scalar("SELECT blob_uri FROM task_checkpoints WHERE run_id=$1"));
        Assert.Equal(1L, await fixture.Runtime.Scalar("SELECT count(*) FROM task_checkpoints WHERE run_id=$1"));
        var conflicting = await fixture.Upload("""{"position":"conflict"}""", 3);
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Accept(conflicting));
        Assert.Equal(last.Sha256, await fixture.Runtime.Scalar("SELECT sha256 FROM task_checkpoints WHERE run_id=$1"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4_999_999)]
    [InlineData(5_000_000)]
    public async Task Checkpoint_rate_is_enforced_at_the_exact_five_second_boundary(long microseconds)
    {
        await using var fixture = await Fixture.Create(database);
        var first = await fixture.Upload("{}");
        await fixture.Accept(first);
        fixture.Runtime.Clock.Now = fixture.Runtime.Clock.Now.AddTicks(microseconds * 10);
        var second = await fixture.Upload("""{"next":true}""", 2);
        if (microseconds < 5_000_000) await Assert.ThrowsAsync<ArgumentException>(() => fixture.Accept(second));
        else await fixture.Accept(second);
        Assert.Equal(microseconds < 5_000_000 ? 1L : 2L,
            await fixture.Runtime.Scalar("SELECT sequence FROM task_checkpoints WHERE run_id=$1"));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("json")]
    public async Task Missing_corrupt_or_non_JSON_blobs_cannot_publish_a_checkpoint_pointer(string failure)
    {
        await using var fixture = await Fixture.Create(database);
        var saved = await fixture.Upload(failure == "json" ? "{" : "{}");
        if (failure == "missing") File.Delete(fixture.PathFor(saved));
        if (failure == "corrupt") await File.WriteAllTextAsync(fixture.PathFor(saved), "[]", Token);
        if (failure == "json") await Assert.ThrowsAsync<ArgumentException>(() => fixture.Accept(saved));
        else if (failure == "missing") await Assert.ThrowsAsync<FileNotFoundException>(() => fixture.Accept(saved));
        else await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Accept(saved));
        Assert.Equal(0L, await fixture.Runtime.Scalar("SELECT count(*) FROM task_checkpoints WHERE run_id=$1"));
    }

    [Theory]
    [InlineData("machine-fence")]
    [InlineData("expired-reservation")]
    [InlineData("closing-reservation")]
    public async Task Physical_fences_discard_old_checkpoints_without_events_pointers_or_reservation_release(string stale)
    {
        await using var fixture = await Fixture.Create(database, reserve: stale != "machine-fence");
        var saved = await fixture.Upload("{}");
        if (stale == "machine-fence")
            await fixture.Runtime.Execute("UPDATE agents SET reservation_epoch=reservation_epoch+1 WHERE id=$1",
                fixture.Runtime.Session.AgentId);
        else
        {
            if (stale == "expired-reservation")
                fixture.Runtime.Clock.Now = fixture.Runtime.Clock.Now.AddMinutes(1);
            else
            {
                using var leases = new ReservationLeaseService(database.DataSource, fixture.Runtime.Store,
                    fixture.Runtime.Clock, NullLogger<ReservationLeaseService>.Instance);
                await leases.ReleaseAsync(fixture.Runtime.RunId, "work", "checkpoint fence test", Token);
            }
        }
        var before = (await fixture.Runtime.Load()).Snapshot.LastSequence;
        await fixture.Accept(saved);
        Assert.Equal(before, (await fixture.Runtime.Load()).Snapshot.LastSequence);
        Assert.Equal(0L, await fixture.Runtime.Scalar("SELECT count(*) FROM task_checkpoints WHERE run_id=$1"));
        if (stale != "machine-fence")
            Assert.Equal(1L, await fixture.Runtime.Scalar("SELECT count(*) FROM reservations WHERE run_id=$1 AND released_at IS NULL"));
    }

    [Fact]
    public async Task Superseded_agent_session_cannot_publish_but_the_current_reconnected_session_can()
    {
        await using var fixture = await Fixture.Create(database);
        var saved = await fixture.Upload("{}");
        using var registry = new AgentRegistry(database.DataSource, fixture.Runtime.Clock);
        var current = await registry.RegisterAsync(fixture.Hello(), Token);
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Accept(saved));
        await fixture.Applier.AcceptAsync(current, saved, Token);
        Assert.Equal(1L, await fixture.Runtime.Scalar("SELECT sequence FROM task_checkpoints WHERE run_id=$1"));
    }

    [Fact]
    public async Task Replacement_keeps_the_checkpoint_but_only_the_new_attempt_can_advance_it()
    {
        await using var fixture = await Fixture.Create(database);
        var first = await fixture.Upload("""{"position":1}""");
        await fixture.Accept(first);
        var next = await fixture.Runtime.AddAgent();
        await fixture.Runtime.LoseAndReplace(fixture.Runtime.Session);
        var replacement = Assert.Single(await fixture.Runtime.Pending(next)).Command;
        var resume = await fixture.Applier.LoadForDispatchAsync(replacement, next, Token);
        Assert.Equal(CheckpointProtocol.Validate(first).SavedAt, resume!.Reference.SavedAt.ToDateTimeOffset());
        Assert.Equal(resume.ToByteString(),
            (await fixture.Applier.LoadForDispatchAsync(replacement, next, Token))!.ToByteString());
        fixture.Runtime.Clock.Now = fixture.Runtime.Clock.Now.AddSeconds(5);
        var stale = await fixture.Upload("""{"position":99}""", 99);
        await fixture.Accept(stale);
        Assert.Equal(1L, await fixture.Runtime.Scalar("SELECT sequence FROM task_checkpoints WHERE run_id=$1"));
        var second = await fixture.Upload("""{"position":2}""", 2, replacement);
        await fixture.Applier.AcceptAsync(next, second, Token);
        await fixture.Accept(first);
        Assert.Equal(2L, await fixture.Runtime.Scalar("SELECT sequence FROM task_checkpoints WHERE run_id=$1"));
        Assert.Equal((short)2, await fixture.Runtime.Scalar("SELECT attempt FROM task_checkpoints WHERE run_id=$1"));
        Assert.Equal(second.BlobKey, await fixture.Runtime.Scalar("SELECT blob_uri FROM task_checkpoints WHERE run_id=$1"));
        Assert.Null(await fixture.Applier.LoadForDispatchAsync(replacement, next, Token));
        Assert.Equal(CheckpointProtocol.Validate(first).SavedAt, new DateTimeOffset(Assert.IsType<DateTime>(
            await fixture.Runtime.Scalar("SELECT resumed_from_checkpoint_at FROM task_attempts WHERE run_id=$1 AND attempt=2"))));
    }

    [Fact]
    public async Task New_checkpoints_are_rejected_after_completion_but_the_accepted_record_remains_replayable()
    {
        await using var fixture = await Fixture.Create(database);
        var first = await fixture.Upload("{}");
        await fixture.Accept(first);
        await fixture.Runtime.Complete(fixture.Dispatch);
        await fixture.Runtime.Finish();
        await fixture.Accept(first);
        fixture.Runtime.Clock.Now = fixture.Runtime.Clock.Now.AddSeconds(5);
        var after = await fixture.Upload("""{"late":true}""", 2);
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Accept(after));
        Assert.Equal(1L, await fixture.Runtime.Scalar("SELECT sequence FROM task_checkpoints WHERE run_id=$1"));
    }

    [Theory]
    [InlineData("session")]
    [InlineData("epoch")]
    [InlineData("terminal")]
    [InlineData("conflict")]
    public async Task Resume_attribution_rejects_stale_or_conflicting_delivery_without_changing_the_record(string mode)
    {
        await using var fixture = await Fixture.Create(database);
        var saved = await fixture.Upload("{}");
        await fixture.Accept(saved);
        var next = await fixture.Runtime.AddAgent();
        await fixture.Runtime.LoseAndReplace(fixture.Runtime.Session);
        var command = Assert.Single(await fixture.Runtime.Pending(next)).Command;
        if (mode == "session") next = next with { SessionId = Guid.NewGuid() };
        if (mode == "epoch") command.ReservationEpoch++;
        if (mode == "terminal")
            await fixture.Runtime.Execute("UPDATE task_attempts SET status=6 WHERE run_id=$1 AND attempt=2", fixture.Runtime.RunId);
        if (mode == "conflict")
            await fixture.Runtime.Execute("""
                UPDATE task_attempts SET resumed_from_checkpoint_at=$2 WHERE run_id=$1 AND attempt=2
                """, fixture.Runtime.RunId, CheckpointProtocol.Validate(saved).SavedAt.AddSeconds(-1));
        if (mode is "session" or "epoch")
            await Assert.ThrowsAsync<ArgumentException>(() => fixture.Applier.LoadForDispatchAsync(command, next, Token));
        else
            await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Applier.LoadForDispatchAsync(command, next, Token));
        var recorded = await fixture.Runtime.Scalar(
            "SELECT resumed_from_checkpoint_at FROM task_attempts WHERE run_id=$1 AND attempt=2");
        if (mode == "conflict")
            Assert.Equal(CheckpointProtocol.Validate(saved).SavedAt.AddSeconds(-1),
                new DateTimeOffset(Assert.IsType<DateTime>(recorded)));
        else
            Assert.Equal(DBNull.Value, recorded);
    }

    [Fact]
    public async Task Failed_index_write_rolls_back_and_retry_commits_the_already_uploaded_blob_once()
    {
        await using var fixture = await Fixture.Create(database);
        var saved = await fixture.Upload("{}");
        await fixture.Runtime.Execute("""
            CREATE FUNCTION reject_checkpoint() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'injected checkpoint index failure'; END $$;
            CREATE TRIGGER reject_checkpoint BEFORE INSERT ON task_checkpoints FOR EACH ROW EXECUTE FUNCTION reject_checkpoint()
            """);
        try
        {
            await Assert.ThrowsAsync<Npgsql.PostgresException>(() => fixture.Accept(saved));
            Assert.Equal(0L, await fixture.Runtime.Scalar("SELECT count(*) FROM task_checkpoints WHERE run_id=$1"));
            Assert.True(File.Exists(fixture.PathFor(saved)));
        }
        finally
        {
            await fixture.Runtime.Execute("DROP TRIGGER reject_checkpoint ON task_checkpoints; DROP FUNCTION reject_checkpoint()");
        }
        await fixture.Accept(saved);
        Assert.Equal(1L, await fixture.Runtime.Scalar("SELECT count(*) FROM task_checkpoints WHERE run_id=$1"));
    }

    [Fact]
    public async Task Exactly_one_MiB_is_verified_and_indexed_without_creating_run_state_events()
    {
        await using var fixture = await Fixture.Create(database);
        var saved = await fixture.Upload("\"" + new string('a', CheckpointProtocol.MaximumBytes - 2) + "\"");
        var before = (await fixture.Runtime.Load()).Snapshot.LastSequence;
        await fixture.Accept(saved);
        Assert.Equal((long)CheckpointProtocol.MaximumBytes,
            await fixture.Runtime.Scalar("SELECT size_bytes FROM task_checkpoints WHERE run_id=$1"));
        Assert.Equal(before, (await fixture.Runtime.Load()).Snapshot.LastSequence);
    }

    [Fact]
    public async Task Current_active_attempt_can_checkpoint_during_abort_before_its_physical_completion()
    {
        await using var fixture = await Fixture.Create(database);
        var run = await fixture.Runtime.Load();
        var abort = new AbortRecord("operator", "Operator", fixture.Runtime.Clock.Now, "checkpoint abort", AbortSource.API);
        await fixture.Runtime.Store.AppendAsync(run, run.CreateEvent(EventEntity.Run, null,
            (int)RunStatus.Aborting, fixture.Runtime.Clock.Now, abort), Token);
        var saved = await fixture.Upload("""{"remoteCancelConfirmed":true}""");
        await fixture.Accept(saved);
        Assert.Equal(1L, await fixture.Runtime.Scalar("SELECT sequence FROM task_checkpoints WHERE run_id=$1"));
        Assert.Equal(RunStatus.Aborting, (await fixture.Runtime.Load()).Snapshot.Status);
    }

    [Fact]
    public async Task Retention_deletes_checkpoint_rows_and_all_run_blobs_but_preserves_active_checkpoints()
    {
        await using var expired = await Fixture.Create(database);
        var old = await expired.Upload("""{"old":true}""");
        await expired.Accept(old);
        expired.Runtime.Clock.Now = expired.Runtime.Clock.Now.AddSeconds(5);
        var latest = await expired.Upload("""{"latest":true}""", 2);
        await expired.Accept(latest);
        expired.Runtime.Clock.Now = expired.Runtime.Clock.Now.AddSeconds(5);
        var unacknowledged = await expired.Upload("""{"uploadedOnly":true}""", 3);
        await expired.Runtime.Complete(expired.Dispatch);
        await expired.Runtime.Finish();
        await using var active = await Fixture.Create(database, expired);
        var current = await active.Upload("""{"live":true}""");
        await active.Accept(current);
        var future = expired.Runtime.Clock.Now.AddDays(100);
        var clock = new FixedClock(new(future.Year, future.Month, future.Day, 3, 0, 0, TimeSpan.Zero));
        var purger = new RetentionPurger(database.DataSource, expired.Blobs, clock, NullLogger<RetentionPurger>.Instance);
        Assert.True(await purger.PurgeBatchAsync(Token) >= 1);
        Assert.Equal(0L, await expired.Runtime.Scalar("SELECT count(*) FROM task_checkpoints WHERE run_id=$1"));
        Assert.Equal(0L, await expired.Runtime.Scalar("SELECT count(*) FROM task_attempts WHERE run_id=$1"));
        Assert.True(await purger.DeleteBlobBatchAsync(Token) >= 1);
        foreach (var saved in new[] { old, latest, unacknowledged })
            Assert.False(File.Exists(expired.PathFor(saved)));
        Assert.Equal(1L, await active.Runtime.Scalar("SELECT count(*) FROM task_checkpoints WHERE run_id=$1"));
        using var verified = new MemoryStream();
        await active.Blobs.DownloadAsync(Content(current), verified, Token);
        Assert.Equal("""{"live":true}""", Encoding.UTF8.GetString(verified.ToArray()));
    }

    private static BlobContent Content(CheckpointSaved saved) =>
        new(BlobKind.Checkpoints, saved.Sha256, checked((long)saved.SizeBytes), saved.BlobKey);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public Runtime Runtime { get; }
        public Dispatch Dispatch { get; }
        public FileSystemBlobStore Blobs { get; }
        public CheckpointApplier Applier { get; }
        public string Root { get; }
        private readonly bool ownsStorage;

        private Fixture(HostedExecutionDatabase database, Runtime runtime, Dispatch dispatch, Fixture? storage)
        {
            Runtime = runtime;
            Dispatch = dispatch;
            Root = storage?.Root ?? Path.Combine(Path.GetTempPath(), "wf-checkpoint-" + Guid.NewGuid().ToString("N"));
            ownsStorage = storage is null;
            Blobs = storage?.Blobs ?? new FileSystemBlobStore(Root, RandomNumberGenerator.GetBytes(32));
            Applier = new(database.DataSource, runtime.Store, Blobs, NullLogger<CheckpointApplier>.Instance);
        }

        public static async Task<Fixture> Create(HostedExecutionDatabase database, Fixture? storage = null, bool reserve = false)
        {
            var runtime = await Runtime.Create(database, w => w.Node("work", n => n.Task("task", _ => { })));
            try
            {
                if (reserve)
                    await new ReservationManager(database.DataSource, runtime.Store, runtime.Clock).ReserveAsync(runtime.RunId,
                        "work", 1, new(machineIds: [runtime.Session.AgentId]), TimeSpan.FromMinutes(1), Token);
                await runtime.Tick();
                return new(database, runtime, Assert.Single(await runtime.Pending()), storage);
            }
            catch { await runtime.DisposeAsync(); throw; }
        }

        public async Task<CheckpointSaved> Upload(string json, ulong sequence = 1, Dispatch? dispatch = null)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            var target = dispatch ?? Dispatch;
            var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var saved = new CheckpointSaved
            {
                Task = target.Task.Clone(), ReservationEpoch = target.ReservationEpoch, CheckpointSequence = sequence,
                SavedAt = Timestamp.FromDateTimeOffset(Runtime.Clock.Now), Sha256 = sha, SizeBytes = (ulong)bytes.Length,
                BlobKey = CheckpointProtocol.Key(Runtime.RunId, $"{target.Task.NodeKey}/{target.Task.TaskKey}", (long)sequence, sha)
            };
            await Blobs.UploadAsync(Content(saved), new MemoryStream(bytes, writable: false), Token);
            return saved;
        }

        public Task Accept(CheckpointSaved saved) => Applier.AcceptAsync(Runtime.Session, saved, Token);
        public string PathFor(CheckpointSaved saved) => Path.Combine([Root, .. saved.BlobKey.Split('/')]);
        public Hello Hello()
        {
            var team = Runtime.Workflow.Split('/')[0];
            var hello = new Hello
            {
                Id = Runtime.Session.AgentId.ToString("D"), Name = team, Pool = team, Os = "windows",
                Arch = "x64", Cores = 4, RamMb = 16384, MaxTasks = 4, Version = "1.0.0", ProtocolVersion = 1
            };
            hello.Inflight.Add(Dispatch.Task.Clone());
            return hello;
        }

        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync();
            if (ownsStorage)
            {
                Blobs.Dispose();
                if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            }
        }
    }
}
