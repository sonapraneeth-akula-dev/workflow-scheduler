using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Npgsql;
using Wf.Contracts;
using Wf.Contracts.Events;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Leadership;
using Wf.ControlPlane.Engine.Maintenance;

namespace Wf.ControlPlane.Engine.Events;

public sealed record GroupCommitReceipt(long BatchId, int EventCount, TimeSpan PersistenceTime);

public sealed class GroupCommitAppender : IAsyncDisposable
{
    private readonly NpgsqlDataSource dataSource;
    private readonly ILogger<GroupCommitAppender> logger;
    private readonly TimeProvider time;
    private readonly LeaseManager? leadership;
    private readonly TimeSpan window;
    private readonly int maxBatchSize;
    private readonly Channel<Request> requests;
    private readonly CancellationTokenSource shutdown = new();
    private readonly Task worker;
    private long batchId;
    private int disposed;

    public GroupCommitAppender(NpgsqlDataSource dataSource, ILogger<GroupCommitAppender> logger,
        TimeProvider? timeProvider = null, TimeSpan? window = null, int maxBatchSize = 512, int queueCapacity = 256,
        LeaseManager? leadership = null)
    {
        this.dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        time = timeProvider ?? TimeProvider.System;
        this.leadership = leadership;
        this.window = window ?? TimeSpan.Zero;
        if (this.window < TimeSpan.Zero || this.window > TimeSpan.FromMilliseconds(2))
            throw new ArgumentOutOfRangeException(nameof(window), "Group-commit collection window must be between zero and 2 ms.");
        if (maxBatchSize is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(maxBatchSize));
        if (queueCapacity is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(queueCapacity));
        this.maxBatchSize = maxBatchSize;
        requests = Channel.CreateBounded<Request>(new BoundedChannelOptions(queueCapacity)
        {
            SingleReader = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false
        });
        worker = RunAsync();
    }

    public Task<GroupCommitReceipt> AppendAsync(RunEvent change, CancellationToken cancellationToken = default) =>
        AppendAsync([change], cancellationToken);

    public async Task<GroupCommitReceipt> AppendAsync(IReadOnlyList<RunEvent> changes, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(changes);
        cancellationToken.ThrowIfCancellationRequested();
        var lease = leadership?.RequireLease();
        if (changes.Count < 1 || changes.Count > maxBatchSize)
            throw new ArgumentException("An atomic event request must fit the configured batch size.", nameof(changes));
        var events = new Entry[changes.Count];
        var bytes = 0;
        for (var index = 0; index < events.Length; index++)
        {
            var change = changes[index];
            if (change is null || change.RunId == Guid.Empty || change.Sequence < 1 || change.At.Offset != TimeSpan.Zero ||
                !Enum.IsDefined(change.Entity) || change.Entity == EventEntity.Run && change.Key is not null ||
                change.Entity != EventEntity.Run && string.IsNullOrWhiteSpace(change.Key) ||
                change.Entity == EventEntity.Run && !Enum.IsDefined((RunStatus)change.State) ||
                change.Entity != EventEntity.Run && !Enum.IsDefined((ExecutionStatus)change.State))
                throw new ArgumentException("Events require a run, positive sequence, UTC time and valid entity key.", nameof(changes));
            var payload = JsonSerializer.SerializeToUtf8Bytes(change, RunStoreJson.Default.RunEvent);
            bytes = checked(bytes + payload.Length);
            if (bytes > 64 * 1024)
                throw new ArgumentException("An atomic event request exceeds 64 KiB.", nameof(changes));
            events[index] = new Entry(change, payload);
        }
        var request = new Request(events, lease, cancellationToken);
        await requests.Writer.WriteAsync(request, cancellationToken).ConfigureAwait(false);
        return await request.Completed.Task.ConfigureAwait(false);
    }

    private async Task RunAsync()
    {
        Request? deferred = null;
        while (deferred is not null || await requests.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            var first = deferred ?? await requests.Reader.ReadAsync().ConfigureAwait(false);
            deferred = null;
            if (window > TimeSpan.Zero) await Task.Delay(window, time).ConfigureAwait(false);
            var batch = new List<Request> { first };
            var size = first.Events.Length;
            while (size < maxBatchSize && requests.Reader.TryRead(out var next))
            {
                if (size + next.Events.Length > maxBatchSize) { deferred = next; break; }
                batch.Add(next);
                size += next.Events.Length;
            }
            foreach (var cancelled in batch.Where(item => item.Cancellation.IsCancellationRequested))
                cancelled.Completed.TrySetCanceled(cancelled.Cancellation);
            var active = batch.Where(item => !item.Completed.Task.IsCompleted).ToArray();
            if (active.Length == 0) continue;
            try
            {
                // After persistence begins, cancellation cannot turn a committed write into a cancelled acknowledgement.
                if (active.Any(request => request.Lease != active[0].Lease))
                    throw new NotLeaderException(leadership?.LeaderHint ?? "");
                var receipt = await PersistWithRetryAsync(active.SelectMany(item => item.Events).ToArray(), active[0].Lease).ConfigureAwait(false);
                foreach (var request in active) request.Completed.TrySetResult(receipt);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Group commit failed; none of its {RequestCount} requests was acknowledged.", active.Length);
                foreach (var request in active) request.Completed.TrySetException(exception);
            }
        }
    }

    private async Task<GroupCommitReceipt> PersistWithRetryAsync(Entry[] entries, LeaseToken? lease)
    {
        var started = Stopwatch.GetTimestamp();
        var delay = TimeSpan.FromMilliseconds(100);
        while (true)
        {
            shutdown.Token.ThrowIfCancellationRequested();
            if (leadership?.Current is { } current && current != lease)
                throw new NotLeaderException(leadership.LeaderHint);
            try
            {
                var receipt = await PersistAsync(entries, lease).ConfigureAwait(false);
                return receipt with { PersistenceTime = Stopwatch.GetElapsedTime(started) };
            }
            catch (Exception exception) when (exception is NpgsqlException { IsTransient: true } or TimeoutException or IOException)
            {
                logger.LogWarning(exception, "Durable group commit is unavailable; retaining {EventCount} events and retrying in {Delay}.",
                    entries.Length, delay);
                await Task.Delay(delay, time, shutdown.Token).ConfigureAwait(false);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 1000));
            }
        }
    }

    private async Task<GroupCommitReceipt> PersistAsync(Entry[] entries, LeaseToken? lease)
    {
        var started = Stopwatch.GetTimestamp();
        var cancellationToken = shutdown.Token;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (leadership is not null)
            await leadership.FenceAsync(connection, transaction, lease ?? throw new NotLeaderException(leadership.LeaderHint), cancellationToken)
                .ConfigureAwait(false);
        await using (var synchronous = new NpgsqlCommand("SET LOCAL synchronous_commit=on", connection))
            await synchronous.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("""
            WITH input AS (
                SELECT uuid_v7_run_day(run_id) AS run_day,* FROM unnest(
                    $1::uuid[],$2::bigint[],$3::timestamptz[],$4::smallint[],$5::text[],$6::bytea[],$7::smallint[]
                ) AS e(run_id,seq,ts,kind,entity_key,payload,state)
            ), expected AS (
                SELECT run_day,run_id,min(seq)-1 AS before_seq,max(seq) AS after_seq,count(*) AS event_count
                FROM input GROUP BY run_day,run_id
            ), advanced AS (
                UPDATE runs r SET last_seq=e.after_seq,
                    status=COALESCE((SELECT i.state FROM input i WHERE i.run_id=r.id AND i.kind=0 ORDER BY i.seq DESC LIMIT 1),r.status)
                FROM expected e WHERE r.run_day=e.run_day AND r.id=e.run_id
                    AND r.last_seq=e.before_seq AND e.after_seq-e.before_seq=e.event_count AND r.status IN (0,1,2)
                RETURNING r.run_day,r.id
            )
            INSERT INTO run_events(run_day,run_id,seq,ts,kind,entity_key,payload)
            SELECT i.run_day,i.run_id,i.seq,i.ts,i.kind,i.entity_key,i.payload
            FROM input i JOIN advanced a ON a.run_day=i.run_day AND a.id=i.run_id
            """, connection);
        command.Parameters.AddWithValue(entries.Select(entry => entry.Event.RunId).ToArray());
        command.Parameters.AddWithValue(entries.Select(entry => entry.Event.Sequence).ToArray());
        command.Parameters.AddWithValue(entries.Select(entry => entry.Event.At).ToArray());
        command.Parameters.AddWithValue(entries.Select(entry => checked((short)entry.Event.Entity)).ToArray());
        command.Parameters.AddWithValue(entries.Select(entry => entry.Event.Key).ToArray());
        command.Parameters.AddWithValue(entries.Select(entry => entry.Payload).ToArray());
        command.Parameters.AddWithValue(entries.Select(entry => checked((short)entry.Event.State)).ToArray());
        var inserted = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (inserted != entries.Length)
        {
            // COMMIT may have succeeded before the connection died. Only byte-identical full replays may be acknowledged.
            await using var replay = new NpgsqlCommand("""
                SELECT count(*) FROM unnest($1::uuid[],$2::bigint[],$3::bytea[]) AS i(run_id,seq,payload)
                JOIN run_events e ON e.run_day=uuid_v7_run_day(i.run_id) AND e.run_id=i.run_id AND e.seq=i.seq
                WHERE e.payload=i.payload
                """, connection);
            replay.Parameters.AddWithValue(entries.Select(entry => entry.Event.RunId).ToArray());
            replay.Parameters.AddWithValue(entries.Select(entry => entry.Event.Sequence).ToArray());
            replay.Parameters.AddWithValue(entries.Select(entry => entry.Payload).ToArray());
            if (inserted != 0 || (long)(await replay.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L) != entries.Length)
                throw new InvalidOperationException("Group commit rejected missing, terminal, stale or noncontiguous run events; the entire batch was rolled back.");
        }
        if (inserted == entries.Length)
        {
            var terminal = RollupWriter.TerminalRunIds(entries.Select(entry => entry.Event));
            foreach (var id in terminal)
            {
                var run = await RunStore.LoadAsync(connection, id, false,
                    message => logger.LogWarning("{RunStateError}", message), cancellationToken).ConfigureAwait(false);
                await RunStore.ProjectTerminalAsync(connection, run, cancellationToken).ConfigureAwait(false);
            }
            if (terminal.Length != 0)
                await RollupWriter.RefreshAsync(connection, terminal, cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(Interlocked.Increment(ref batchId), entries.Length, Stopwatch.GetElapsedTime(started));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) { await worker.ConfigureAwait(false); return; }
        requests.Writer.TryComplete();
        if (await Task.WhenAny(worker, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false) != worker)
            await shutdown.CancelAsync().ConfigureAwait(false);
        await worker.ConfigureAwait(false);
        shutdown.Dispose();
    }

    private sealed record Entry(RunEvent Event, byte[] Payload);
    private sealed class Request(Entry[] events, LeaseToken? lease, CancellationToken cancellation)
    {
        public Entry[] Events { get; } = events;
        public LeaseToken? Lease { get; } = lease;
        public CancellationToken Cancellation { get; } = cancellation;
        public TaskCompletionSource<GroupCommitReceipt> Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
