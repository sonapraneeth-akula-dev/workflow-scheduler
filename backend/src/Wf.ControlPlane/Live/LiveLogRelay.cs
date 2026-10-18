using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Threading.Channels;
using Azure;
using Google.Protobuf.WellKnownTypes;
using Npgsql;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Logs;
using Wf.ControlPlane.Engine.Agents;
using Wf.ControlPlane.Storage;

namespace Wf.ControlPlane.Live;

/// <summary>PostgreSQL carries expiring interests and immutable object references, never log content.</summary>
public sealed class LiveLogRelay(NpgsqlDataSource source, LiveSubscriptions subscriptions, IBlobStore blobs,
    TimeProvider clock, ILogger<LiveLogRelay> logger) : BackgroundService
{
    private readonly Guid owner = Guid.NewGuid();
    private readonly object gate = new();
    private readonly Dictionary<(Guid Owner, string Id), DateTimeOffset> interests = [];
    private readonly Dictionary<string, DateTimeOffset> retiring = new(StringComparer.Ordinal);
    private readonly Channel<LiveLogPointer> pending = Channel.CreateBounded<LiveLogPointer>(256);
    private readonly Channel<bool> publishChanged = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Channel<bool> changed = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    { FullMode = BoundedChannelFullMode.DropWrite });
    private int reset;
    private int resend = 1;
    private int ready;

    public bool IsReady => Volatile.Read(ref ready) != 0;
    public void InterestsChanged() => changed.Writer.TryWrite(true);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var tasks = new[] { ListenAsync(lifetime.Token), MaintainAsync(lifetime.Token), PublishAsync(lifetime.Token),
            CleanupAsync(lifetime.Token) };
        await Task.WhenAny(tasks);
        await lifetime.CancelAsync();
        try { await Task.WhenAll(tasks); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { Volatile.Write(ref ready, 0); }
    }

    private async Task ListenAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var listener = await source.OpenConnectionAsync(token);
                listener.Notification += (_, notification) => Receive(notification.Channel, notification.Payload);
                await using (var command = new NpgsqlCommand(
                    "SET application_name='wf-live-logs'; LISTEN live_log_interest; LISTEN live_log_batches; LISTEN live_log_index", listener))
                    await command.ExecuteNonQueryAsync(token);
                Volatile.Write(ref ready, 1);
                Interlocked.Exchange(ref resend, 1);
                Interlocked.Exchange(ref reset, 1);
                publishChanged.Writer.TryWrite(true);
                InterestsChanged();
                while (!token.IsCancellationRequested)
                    if (!await listener.WaitAsync(5000, token))
                    {
                        await using var ping = new NpgsqlCommand("SELECT 1", listener);
                        await ping.ExecuteScalarAsync(token);
                    }
            }
            catch (Exception error) when (error is NpgsqlException { IsTransient: true } or IOException or TimeoutException)
            {
                Volatile.Write(ref ready, 0);
                lock (gate) interests.Clear();
                logger.LogError(error, "Live log notifications disconnected; resynchronization required.");
                await Task.Delay(TimeSpan.FromSeconds(1), clock, token);
            }
        }
    }

    private void Receive(string channel, string payload)
    {
        try
        {
            if (channel == "live_log_index")
            {
                if (subscriptions.LogInterests.Any(id => LogIndex.PrefixFor(LiveLogProtocol.Identity(id)) == payload))
                    Interlocked.Exchange(ref reset, 1);
            }
            else if (channel == "live_log_interest")
            {
                var interest = JsonSerializer.Deserialize(payload, LiveLogJson.Default.LiveLogInterest)
                    ?? throw new InvalidDataException("Empty live log interest.");
                _ = LiveLogProtocol.Identity(interest.Id);
                if (interest.Owner == Guid.Empty) throw new InvalidDataException("Invalid log interest owner.");
                lock (gate)
                {
                    if (!interest.On) interests.Remove((interest.Owner, interest.Id));
                    else
                    {
                        if (interests.Count >= 10000 && !interests.ContainsKey((interest.Owner, interest.Id)))
                        {
                            logger.LogWarning("Live log interest limit reached; clients must resynchronize.");
                            Interlocked.Exchange(ref reset, 1);
                        }
                        else interests[(interest.Owner, interest.Id)] = clock.GetUtcNow() + LiveLogProtocol.InterestLifetime;
                    }
                }
            }
            else
            {
                var pointer = JsonSerializer.Deserialize(payload, LiveLogJson.Default.LiveLogPointer)
                    ?? throw new InvalidDataException("Empty live log pointer.");
                _ = LiveLogProtocol.Identity(pointer.Id);
                if (subscriptions.HasSubscribers("logs", pointer.Id) && !pending.Writer.TryWrite(pointer))
                {
                    logger.LogWarning("Live log publication exceeded 256 pending batches; requesting durable resynchronization.");
                    Interlocked.Exchange(ref reset, 1);
                }
            }
            publishChanged.Writer.TryWrite(true);
            InterestsChanged();
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidDataException)
        {
            logger.LogError(error, "Invalid live log notification; requesting durable resynchronization.");
            Interlocked.Exchange(ref reset, 1);
            publishChanged.Writer.TryWrite(true);
        }
    }

    internal void Retire(string key)
    {
        lock (gate)
        {
            if (retiring.Count >= 10000 && !retiring.ContainsKey(key))
                throw new IOException("Ephemeral log cleanup queue is full; durable logs remain available.");
            retiring[key] = clock.GetUtcNow().AddSeconds(30);
        }
    }

    public async Task IndexUploadedAsync(string prefix, CancellationToken token)
    {
        await using var command = source.CreateCommand("SELECT pg_notify('live_log_index',$1)");
        command.Parameters.AddWithValue(prefix);
        await command.ExecuteNonQueryAsync(token);
    }

    private async Task CleanupAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), clock);
        while (await timer.WaitForNextTickAsync(token))
        {
            KeyValuePair<string, DateTimeOffset>[] expired;
            lock (gate) expired = retiring.Where(pair => pair.Value <= clock.GetUtcNow()).ToArray();
            await Parallel.ForEachAsync(expired, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = token },
                async (pair, cancellation) =>
                {
                    try
                    {
                        await blobs.DeleteLiveLogAsync(pair.Key, cancellation);
                        lock (gate)
                            if (retiring.TryGetValue(pair.Key, out var due) && due == pair.Value) retiring.Remove(pair.Key);
                    }
                    catch (Exception error) when (error is IOException or RequestFailedException or UnauthorizedAccessException)
                    {
                        logger.LogError(error, "Ephemeral log object cleanup failed; retaining its bounded retry entry.");
                    }
                });
        }
    }

    private async Task MaintainAsync(CancellationToken token)
    {
        var previous = new HashSet<string>(StringComparer.Ordinal);
        var refreshAt = DateTimeOffset.MinValue;
        while (!token.IsCancellationRequested)
        {
            var ids = subscriptions.LogInterests.ToHashSet(StringComparer.Ordinal);
            var refresh = Interlocked.Exchange(ref resend, 0) != 0 || clock.GetUtcNow() >= refreshAt;
            try
            {
                foreach (var id in previous.Except(ids))
                    await NotifyAsync(new(owner, id, false), token);
                foreach (var id in ids.Where(id => refresh || !previous.Contains(id)))
                    await NotifyAsync(new(owner, id, true), token);
                previous = ids;
                if (refresh) refreshAt = clock.GetUtcNow().AddSeconds(5);
                lock (gate)
                    foreach (var expired in interests.Where(pair => pair.Value <= clock.GetUtcNow()).Select(pair => pair.Key).ToArray())
                        interests.Remove(expired);
            }
            catch (Exception error) when (error is NpgsqlException { IsTransient: true } or IOException or TimeoutException)
            {
                Interlocked.Exchange(ref resend, 1);
                logger.LogError(error, "Live log interest update failed; expiring leases prevent unattended mirroring.");
            }
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
            wait.CancelAfter(LiveLogProtocol.BatchInterval);
            try { await changed.Reader.ReadAsync(wait.Token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        }
    }

    private async Task NotifyAsync(LiveLogInterest interest, CancellationToken token)
    {
        await using var command = source.CreateCommand("SELECT pg_notify('live_log_interest',$1)");
        command.Parameters.AddWithValue(JsonSerializer.Serialize(interest, LiveLogJson.Default.LiveLogInterest));
        await command.ExecuteNonQueryAsync(token);
    }

    internal bool HasInterest(AttemptRef task)
    {
        var id = LiveLogProtocol.Identity(task);
        lock (gate) return interests.Any(pair => pair.Key.Id == id && pair.Value > clock.GetUtcNow());
    }

    public async Task<IReadOnlyList<MirrorLogs>> PendingAsync(AgentSession session,
        IReadOnlyDictionary<string, MirrorLogs> sent, CancellationToken token)
    {
        Dictionary<string, DateTimeOffset> desired;
        var now = clock.GetUtcNow();
        lock (gate) desired = interests.Where(pair => pair.Value > now)
            .GroupBy(pair => pair.Key.Id).ToDictionary(group => group.Key, group => group.Max(pair => pair.Value));
        if (desired.Count == 0 && sent.Count == 0) return [];
        var active = new Dictionary<string, MirrorLogs>(StringComparer.Ordinal);
        ulong epoch;
        await using (var query = source.CreateCommand("SELECT reservation_epoch FROM agents WHERE id=$1 AND session_id=$2"))
        {
            query.Parameters.AddWithValue(session.AgentId);
            query.Parameters.AddWithValue(session.SessionId);
            if (await query.ExecuteScalarAsync(token) is not long current)
                throw new ArgumentException("Live log command agent session was superseded.");
            epoch = checked((ulong)current);
        }
        if (desired.Count > 0)
        {
            await using var command = source.CreateCommand("""
                SELECT t.run_id,t.task_key,t.attempt,t.reservation_epoch
                FROM task_attempts t JOIN agents a ON a.id=t.agent_id
                WHERE t.agent_id=$1 AND a.session_id=$2 AND (t.status IN (2,3,4) OR t.ended_at>now()-interval '2 seconds')
                    AND t.reservation_epoch=a.reservation_epoch AND t.dispatch_payload IS NOT NULL
                """);
            command.Parameters.AddWithValue(session.AgentId);
            command.Parameters.AddWithValue(session.SessionId);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var parts = reader.GetString(1).Split('/');
                if (parts.Length != 2) throw new InvalidDataException("Invalid assigned task log identity.");
                var task = new AttemptRef
                {
                    RunId = reader.GetGuid(0).ToString("D"),
                    NodeKey = parts[0],
                    TaskKey = parts[1],
                    Attempt = checked((uint)reader.GetInt16(2))
                };
                var id = LiveLogProtocol.Identity(task);
                if (desired.TryGetValue(id, out var expiry) && expiry > clock.GetUtcNow())
                    active[id] = new()
                    {
                        Task = task,
                        On = true,
                        ReservationEpoch = checked((ulong)reader.GetInt64(3)),
                        ExpiresAt = Timestamp.FromDateTimeOffset(expiry)
                    };
            }
        }
        var commands = active.Where(pair => !sent.TryGetValue(pair.Key, out var prior) || !prior.Equals(pair.Value))
            .Select(pair => pair.Value).ToList();
        foreach (var prior in sent.Where(pair => !active.ContainsKey(pair.Key)))
            commands.Add(new()
            {
                Task = prior.Value.Task,
                On = false,
                ReservationEpoch = epoch,
                ExpiresAt = Timestamp.FromDateTimeOffset(now)
            });
        return commands;
    }

    private async Task PublishAsync(CancellationToken token)
    {
        while (await publishChanged.Reader.WaitToReadAsync(token))
        {
            publishChanged.Reader.TryRead(out _);
            if (Interlocked.Exchange(ref reset, 0) != 0)
                foreach (var id in subscriptions.LogInterests)
                    await subscriptions.PublishAsync("logs", id, "Resync", new LiveResync("logs", id), token);
            while (pending.Reader.TryRead(out var pointer))
            {
                if (!subscriptions.HasSubscribers("logs", pointer.Id)) continue;
                try
                {
                    var task = LiveLogProtocol.Identity(pointer.Id);
                    if (pointer.SizeBytes is <= 0 or > LiveLogProtocol.MaximumBatchBytes + 65536 ||
                        pointer.Key != LogIndex.PrefixFor(task) + "live-" + pointer.Sha256 + ".log.gz")
                        throw new InvalidDataException("Invalid live log object reference.");
                    using var compressed = new MemoryStream();
                    await blobs.DownloadAsync(new(BlobKind.Logs, pointer.Sha256, pointer.SizeBytes, pointer.Key), compressed, token);
                    compressed.Position = 0;
                    using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
                    using var raw = new MemoryStream();
                    var buffer = new byte[32768];
                    int count;
                    while ((count = await gzip.ReadAsync(buffer, token)) != 0)
                    {
                        if (raw.Length + count > LiveLogProtocol.MaximumBatchBytes)
                            throw new InvalidDataException("Live log object expands beyond its protocol limit.");
                        raw.Write(buffer, 0, count);
                    }
                    var batch = LiveLogBatch.Parser.ParseFrom(raw.GetBuffer(), 0, checked((int)raw.Length));
                    LiveLogProtocol.Validate(batch);
                    if (LiveLogProtocol.Identity(batch.Task) != pointer.Id)
                        throw new InvalidDataException("Live log object belongs to a different attempt.");
                    var update = new LiveLogUpdate(pointer.Id, batch.Records.Select(record => new LiveLogLine(
                        checked((long)record.Line), record.At.ToDateTimeOffset().ToString("O", CultureInfo.InvariantCulture),
                        record.Stream, record.Level, record.Text)).ToArray(), batch.Gap);
                    await subscriptions.PublishAsync("logs", pointer.Id, "LogBatch", update, token);
                }
                catch (Exception error) when (error is IOException or NpgsqlException { IsTransient: true } or
                    TimeoutException or RequestFailedException or UnauthorizedAccessException or ArgumentException)
                {
                    logger.LogError(error, "Live log batch unavailable; requesting durable attempt-log resynchronization.");
                    await subscriptions.PublishAsync("logs", pointer.Id, "Resync", new LiveResync("logs", pointer.Id), token);
                }
            }
        }
    }
}

public static class LiveLogServices
{
    public static IServiceCollection AddWorkflowLiveLogs(this IServiceCollection services)
    {
        services.AddSingleton<LiveLogRelay>();
        services.AddHostedService(services => services.GetRequiredService<LiveLogRelay>());
        services.AddSingleton<LiveLogSink>();
        return services;
    }
}
