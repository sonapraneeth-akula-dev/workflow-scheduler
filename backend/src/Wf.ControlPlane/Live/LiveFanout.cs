using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Npgsql;
using Wf.ControlPlane.Engine.Dispatch;

namespace Wf.ControlPlane.Live;

public sealed class LiveFanout(NpgsqlDataSource source, LiveSubscriptions subscriptions,
    TimeProvider time, ILogger<LiveFanout> logger) : BackgroundService
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, long> changes = [];
    private readonly Dictionary<Guid, ReportAdded> reports = [];
    private readonly Dictionary<string, LiveRunSummary> lists = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, long> published = [];
    private readonly Channel<bool> changed = Signal();
    private readonly Channel<bool> listChanged = Signal();
    private readonly Channel<bool> reportChanged = Signal();
    private bool resync;
    private bool reauthorize;
    private int ready;

    public bool IsReady => Volatile.Read(ref ready) != 0;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var workers = new[] { ListenAsync(lifetime.Token), PublishAsync(lifetime.Token),
            PublishListsAsync(lifetime.Token), PublishReportsAsync(lifetime.Token) };
        await Task.WhenAny(workers);
        await lifetime.CancelAsync();
        try { await Task.WhenAll(workers); }
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
                listener.Notification += (_, notification) =>
                {
                    lock (gate)
                    {
                        if (notification.Channel == "registry") reauthorize = true;
                        else if (notification.Channel == "report_added")
                        {
                            if (subscriptions.HasAnySubscribers) QueueReport(notification.Payload);
                            reportChanged.Writer.TryWrite(true);
                        }
                        else if (subscriptions.HasAnySubscribers)
                        {
                            var parts = notification.Payload.Split(':');
                            if (parts.Length != 2 || !Guid.TryParseExact(parts[0], "D", out var id) ||
                                !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var sequence))
                                throw new InvalidDataException("Invalid durable live-run notification.");
                            if (changes.Count >= 10000 && !changes.ContainsKey(id))
                            {
                                changes.Clear();
                                resync = true;
                                logger.LogWarning("Live feed exceeded 10000 pending runs; requesting durable client resynchronization.");
                            }
                            changes[id] = changes.TryGetValue(id, out var existing) ? Math.Min(existing, sequence) : sequence;
                        }
                    }
                    changed.Writer.TryWrite(true);
                };
                await using (var listen = new NpgsqlCommand(
                    "SET application_name='wf-live-feed'; LISTEN live_runs; LISTEN registry; LISTEN report_added", listener))
                    await listen.ExecuteNonQueryAsync(token);
                lock (gate) resync = true;
                changed.Writer.TryWrite(true);
                Volatile.Write(ref ready, 1);
                while (!token.IsCancellationRequested)
                {
                    if (!await listener.WaitAsync(5000, token))
                    {
                        // Transport liveness only: no timer-driven state queries or UI updates.
                        await using var heartbeat = new NpgsqlCommand("SELECT 1", listener);
                        await heartbeat.ExecuteScalarAsync(token);
                    }
                }
            }
            catch (Exception exception) when (exception is NpgsqlException { IsTransient: true } or IOException or TimeoutException)
            {
                Volatile.Write(ref ready, 0);
                logger.LogError(exception, "Live notifications disconnected; reconnecting with explicit client resynchronization.");
                await Task.Delay(TimeSpan.FromSeconds(1), time, token);
            }
        }
    }

    private async Task PublishAsync(CancellationToken token)
    {
        while (await changed.Reader.WaitToReadAsync(token))
        {
            changed.Reader.TryRead(out _);
            KeyValuePair<Guid, long>[] pending;
            bool reset;
            bool checkPermissions;
            lock (gate)
            {
                pending = changes.ToArray();
                changes.Clear();
                reset = resync;
                resync = false;
                checkPermissions = reauthorize;
                reauthorize = false;
            }
            try
            {
                if (checkPermissions) await subscriptions.ReauthorizeAsync(token);
                if (reset) await subscriptions.ResyncAsync(token);
                foreach (var (id, sequence) in pending) await PublishRunAsync(id, sequence, token);
            }
            catch (Exception exception) when (exception is NpgsqlException { IsTransient: true } or IOException or TimeoutException)
            {
                logger.LogError(exception, "Live state publication unavailable; retaining a client resynchronization request.");
                lock (gate) resync = true;
                await Task.Delay(TimeSpan.FromSeconds(1), time, token);
                changed.Writer.TryWrite(true);
            }
        }
    }

    private void QueueReport(string payload)
    {
        try
        {
            using var json = JsonDocument.Parse(payload);
            if (json.RootElement.GetProperty("kind").GetString() != "ReportAdded")
                throw new InvalidDataException();
            var runId = json.RootElement.GetProperty("runId").GetGuid();
            var reportId = json.RootElement.GetProperty("reportId").GetGuid();
            if (runId.Version != 7 || reportId.Version != 7) throw new InvalidDataException();
            if (reports.Count >= 10000 && !reports.ContainsKey(reportId))
            {
                reports.Clear();
                resync = true;
                logger.LogWarning("Live report feed exceeded 10000 pending reports; requesting index resynchronization.");
            }
            reports[reportId] = new(runId.ToString("D"), reportId.ToString("D"));
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or KeyNotFoundException or
            InvalidOperationException or FormatException)
        {
            resync = true;
            logger.LogWarning("Invalid report notification; requesting durable index resynchronization.");
        }
    }

    private async Task PublishReportsAsync(CancellationToken token)
    {
        while (await reportChanged.Reader.WaitToReadAsync(token))
        {
            reportChanged.Reader.TryRead(out _);
            ReportAdded[] pending;
            lock (gate)
            {
                pending = reports.Values.ToArray();
                reports.Clear();
            }
            try
            {
                foreach (var report in pending) await PublishReportAsync(report, token);
            }
            catch (Exception error) when (error is NpgsqlException { IsTransient: true } or IOException or TimeoutException)
            {
                logger.LogError(error, "Live report publication unavailable; requesting durable index resynchronization.");
                lock (gate) resync = true;
                changed.Writer.TryWrite(true);
            }
        }
    }

    private async Task PublishReportAsync(ReportAdded report, CancellationToken token)
    {
        if (!subscriptions.HasSubscribers("run", report.RunId)) return;
        await using var query = source.CreateCommand("""
            SELECT EXISTS(SELECT 1 FROM report_ids i JOIN reports r ON r.run_day=i.run_day AND r.id=i.id
                WHERE i.id=$1 AND r.run_id=$2)
            """);
        query.Parameters.AddWithValue(Guid.ParseExact(report.ReportId, "D"));
        query.Parameters.AddWithValue(Guid.ParseExact(report.RunId, "D"));
        if (await query.ExecuteScalarAsync(token) is true)
            await subscriptions.PublishAsync("run", report.RunId, "ReportAdded", report, token);
    }

    private async Task PublishRunAsync(Guid id, long sequence, CancellationToken token)
    {
        if (!subscriptions.HasAnySubscribers) return;
        foreach (var old in published.Keys.Where(run => !subscriptions.HasSubscribers("run", run.ToString("D"))).ToArray())
            published.Remove(old);
        LiveRunSummary summary;
        await using (var query = source.CreateCommand("""
            SELECT r.workflow_id,r.version_id,r.series_id,r.status,r.last_seq,r.created_at,r.ended_at
            FROM runs r JOIN workflow_types t
              ON t.id=split_part(r.workflow_id,'/',1)||'/'||split_part(r.workflow_id,'/',2)
            WHERE r.run_day=uuid_v7_run_day($1) AND r.id=$1 AND r.workflow_id ~ '^[^/]+/[^/]+/[^/]+$'
            """))
        {
            query.Parameters.AddWithValue(id);
            await using var reader = await query.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token)) return;
            summary = new(id.ToString("D"), reader.GetString(0), reader.GetGuid(1).ToString("D"),
                reader.IsDBNull(2) ? null : reader.GetGuid(2).ToString("D"), reader.GetInt16(3), reader.GetInt64(4),
                reader.GetFieldValue<DateTimeOffset>(5).ToString("O", CultureInfo.InvariantCulture),
                reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6).ToString("O", CultureInfo.InvariantCulture));
        }
        if (subscriptions.HasSubscribers("run", summary.RunId))
        {
            var after = Math.Max(Math.Max(1, sequence), published.GetValueOrDefault(id) + 1);
            while (after <= summary.LastSeq)
            {
                var events = new List<LiveEvent>();
                await using (var query = source.CreateCommand("""
                    SELECT payload FROM run_events WHERE run_day=uuid_v7_run_day($1) AND run_id=$1
                      AND seq>=$2 AND seq<=$3 ORDER BY seq LIMIT 512
                    """))
                {
                    query.Parameters.AddWithValue(id);
                    query.Parameters.AddWithValue(after);
                    query.Parameters.AddWithValue(summary.LastSeq);
                    await using var reader = await query.ExecuteReaderAsync(token);
                    while (await reader.ReadAsync(token))
                        events.Add(LiveEvent.From(JsonSerializer.Deserialize(reader.GetFieldValue<byte[]>(0),
                            RunStoreJson.Default.RunEvent) ?? throw new InvalidDataException("A durable live event is null.")));
                }
                if (events.Count == 0 || events[0].Seq != after || events[^1].Seq != after + events.Count - 1)
                    throw new InvalidDataException("The committed live event tail is not contiguous.");
                await subscriptions.PublishAsync("run", summary.RunId, "RunDelta", new RunDelta(summary.RunId, events.ToArray()), token);
                after = events[^1].Seq + 1;
                published[id] = events[^1].Seq;
            }
        }
        lock (gate) lists[summary.RunId] = summary;
        listChanged.Writer.TryWrite(true);
    }

    private async Task PublishListsAsync(CancellationToken token)
    {
        while (await listChanged.Reader.WaitToReadAsync(token))
        {
            listChanged.Reader.TryRead(out _);
            await Task.Delay(TimeSpan.FromMilliseconds(250), time, token);
            LiveRunSummary[] pending;
            lock (gate)
            {
                pending = lists.Values.ToArray();
                lists.Clear();
            }
            try
            {
                var groups = pending.SelectMany(summary =>
                {
                    var segments = summary.WorkflowId.Split('/');
                    var scopes = new List<(string Scope, string Id, LiveRunSummary Summary)>
                    {
                        ("team", segments[0], summary), ("type", segments[0] + "/" + segments[1], summary)
                    };
                    if (summary.SeriesId is { } series) scopes.Add(("series", series, summary));
                    return scopes;
                }).GroupBy(item => (item.Scope, item.Id));
                foreach (var group in groups)
                    await subscriptions.PublishAsync(group.Key.Scope, group.Key.Id, "ListDelta",
                        new ListDelta(group.Key.Scope, group.Key.Id, group.Select(item => item.Summary).ToArray()), token);
            }
            catch (Exception exception) when (exception is NpgsqlException { IsTransient: true } or IOException or TimeoutException)
            {
                logger.LogError(exception, "Live list publication unavailable; requesting durable client resynchronization.");
                lock (gate) resync = true;
                changed.Writer.TryWrite(true);
            }
        }
    }

    private static Channel<bool> Signal() => Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite, AllowSynchronousContinuations = false
    });
}

public static class LiveServiceExtensions
{
    public static IServiceCollection AddWorkflowLive(this IServiceCollection services)
    {
        services.AddSignalR().AddMessagePackProtocol();
        services.AddSingleton<LiveSubscriptions>();
        services.AddSingleton<LiveFanout>();
        services.AddHostedService(services => services.GetRequiredService<LiveFanout>());
        return services;
    }
}
