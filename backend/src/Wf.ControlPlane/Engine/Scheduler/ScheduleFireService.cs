using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Wf.Contracts.Schedules;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Leadership;
using Wf.ControlPlane.Engine.Recovery;
using Wf.ControlPlane.Engine.Registry;
using Wf.Engine.Core;
using Wf.Engine.Core.Scheduling;
using Wf.Validation;
using Wf.Validation.Schedules;

namespace Wf.ControlPlane.Engine.Scheduler;

public sealed class ScheduleFireRejectedException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class ScheduleFireService(NpgsqlDataSource source, LeaseManager leadership, RunRecovery recovery,
    ActiveVersionCache versions, TimeProvider time, ILogger<ScheduleFireService> logger) : BackgroundService
{
    private readonly ScheduleTimerHeap timers = new();
    private LeaseToken? readyLease;
    private static readonly TimeSpan ReloadPeriod = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaximumWait = TimeSpan.FromMilliseconds(100);
    public bool IsReady => Volatile.Read(ref readyLease) is { } lease && leadership.Current == lease &&
        recovery.IsReady && versions.IsReady;

    public async Task<ScheduleTimer?> FireAsync(ScheduleTimer timer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(timer);
        var lease = leadership.RequireLease();
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await leadership.FenceAsync(connection, transaction, lease, cancellationToken);
        Guid series;
        string workflow, timezone, variables;
        string? cron, pin;
        TimeSpan? interval;
        ScheduleOverlapPolicy overlap;
        DateTimeOffset fireAt;
        await using (var read = new NpgsqlCommand("""
            SELECT s.workflow_id,s.timezone,s.cron,s.interval_s,s.variables::text,s.version_pin,
                s.overlap_policy,s.next_fire_at,r.id
            FROM schedules s JOIN workflows w ON w.id=s.workflow_id
            LEFT JOIN run_series r ON r.schedule_id=s.id
            WHERE s.id=$1 AND NOT s.paused AND s.deleted_at IS NULL AND w.deleted_at IS NULL
                AND s.next_fire_at IS NOT NULL
            FOR UPDATE OF s
            """, connection, transaction))
        {
            read.Parameters.AddWithValue(timer.ScheduleId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            workflow = reader.GetString(0);
            timezone = reader.GetString(1);
            cron = reader.IsDBNull(2) ? null : reader.GetString(2);
            interval = reader.IsDBNull(3) ? null : TimeSpan.FromSeconds(reader.GetInt32(3));
            variables = reader.GetString(4);
            pin = reader.IsDBNull(5) ? null : reader.GetString(5);
            overlap = (ScheduleOverlapPolicy)reader.GetInt16(6);
            fireAt = reader.GetFieldValue<DateTimeOffset>(7);
            series = reader.IsDBNull(8) ? throw new InvalidDataException("An active schedule has no permanent run series.") : reader.GetGuid(8);
        }
        if (fireAt != timer.FireAt || fireAt > time.GetUtcNow()) return new(timer.ScheduleId, fireAt);
        var next = ScheduleExpression.Create(cron, interval, timezone).GetNextOccurrence(fireAt);
        bool claimed;
        await using (var claim = new NpgsqlCommand("""
            INSERT INTO schedule_fires(schedule_id,fire_at,outcome) VALUES($1,$2,0)
            ON CONFLICT(schedule_id,fire_at) DO NOTHING RETURNING schedule_id
            """, connection, transaction))
        {
            claim.Parameters.AddWithValue(timer.ScheduleId);
            claim.Parameters.AddWithValue(fireAt);
            claimed = await claim.ExecuteScalarAsync(cancellationToken) is Guid;
        }
        if (claimed)
        {
            int active;
            await using (var count = new NpgsqlCommand("SELECT count(*)::int FROM runs WHERE series_id=$1 AND status IN (0,1,2)",
                connection, transaction))
            {
                count.Parameters.AddWithValue(series);
                active = (int)(await count.ExecuteScalarAsync(cancellationToken)
                    ?? throw new InvalidDataException("The overlap query returned no count."));
            }
            if (OverlapPolicy.Decide(active, overlap) == ScheduleOverlapDecision.Skip)
            {
                await RunStore.ExecuteAsync(connection, """
                    UPDATE schedule_fires SET outcome=1,reason='overlap' WHERE schedule_id=$1 AND fire_at=$2
                    """, cancellationToken, timer.ScheduleId, fireAt);
            }
            else
            {
                SelectedWorkflowVersion selected;
                try
                {
                    selected = await WorkflowVersionResolver.ResolveAsync(versions, source, workflow, pin, true,
                        cancellationToken, connection);
                }
                catch (Exception error) when (error is KeyNotFoundException or ArgumentException or FormatException)
                {
                    throw new ScheduleFireRejectedException($"Schedule '{timer.ScheduleId}' has no valid matching workflow version.", error);
                }
                var values = JsonSerializer.Deserialize(variables, ScheduleFileJsonContext.Default.ImmutableDictionaryStringJsonElement)
                    ?? throw new InvalidDataException("Schedule variables are null.");
                var bound = VariableBinder.Resolve(selected.Manifest.Variables, values, path: "$.schedule.variables",
                    cancellationToken: cancellationToken);
                if (!bound.IsValid)
                    throw new ScheduleFireRejectedException($"Schedule '{timer.ScheduleId}' variables failed validation: " +
                        string.Join("; ", bound.Errors.Select(error => $"{error.RuleId} {error.Path}: {error.Message}")));
                var now = time.GetUtcNow();
                var run = new RunState(Guid.CreateVersion7(now), selected.Manifest.Nodes, selected.Manifest.Metadata,
                    message => logger.LogWarning("{RunStateError}", message), bound.Values);
                await RunStore.CreateAsync(connection, run, workflow, selected.VersionId, now.Add(selected.Manifest.RunTimeout),
                    now, "scheduler", cancellationToken, series, scheduled: true);
                await RunStore.ExecuteAsync(connection, """
                    UPDATE schedule_fires SET run_id=$3 WHERE schedule_id=$1 AND fire_at=$2
                    """, cancellationToken, timer.ScheduleId, fireAt, run.Snapshot.RunId);
            }
        }
        await using (var advance = new NpgsqlCommand("UPDATE schedules SET next_fire_at=$2 WHERE id=$1", connection, transaction))
        {
            advance.Parameters.AddWithValue(timer.ScheduleId);
            advance.Parameters.AddWithValue(NpgsqlDbType.TimestampTz, (object?)next ?? DBNull.Value);
            await advance.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return next is { } at ? new(timer.ScheduleId, at) : null;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await using var listener = await source.OpenConnectionAsync(stoppingToken);
                    var invalidated = true;
                    listener.Notification += (_, notification) =>
                    {
                        if (notification.Channel is "registry" or "schedules") invalidated = true;
                    };
                    await using (var listen = new NpgsqlCommand("LISTEN registry; LISTEN schedules", listener))
                        await listen.ExecuteNonQueryAsync(stoppingToken);
                    LeaseToken? loadedLease = null;
                    var reloadedAt = time.GetTimestamp();
                    while (!stoppingToken.IsCancellationRequested)
                    {
                        var lease = leadership.Current;
                        if (lease is not null && recovery.IsReady && versions.IsReady)
                        {
                            if (invalidated || loadedLease != lease || time.GetElapsedTime(reloadedAt) >= ReloadPeriod)
                            {
                                await ReloadAsync(stoppingToken);
                                loadedLease = lease;
                                Volatile.Write(ref readyLease, lease);
                                invalidated = false;
                                reloadedAt = time.GetTimestamp();
                                await using var heartbeat = new NpgsqlCommand("SELECT 1", listener);
                                await heartbeat.ExecuteScalarAsync(stoppingToken);
                            }
                            var limit = timers.Count;
                            for (var index = 0; index < limit && timers.TryTakeDue(time.GetUtcNow(), out var timer); index++)
                            {
                                try
                                {
                                    if (await FireAsync(timer!, stoppingToken) is { } next) timers.Add(next);
                                }
                                catch (ScheduleFireRejectedException error)
                                {
                                    logger.LogError(error, "Schedule {ScheduleId} cannot fire; its cursor is retained until configuration reload.",
                                        timer!.ScheduleId);
                                }
                            }
                        }
                        else
                        {
                            loadedLease = null;
                            Volatile.Write(ref readyLease, null);
                        }
                        var wait = MaximumWait;
                        if (loadedLease is not null && timers.NextFireAt is { } nextFire)
                            wait = TimeSpan.FromTicks(Math.Clamp((nextFire - time.GetUtcNow()).Ticks,
                                TimeSpan.TicksPerMillisecond, MaximumWait.Ticks));
                        await listener.WaitAsync((int)Math.Ceiling(wait.TotalMilliseconds), stoppingToken);
                    }
                }
                catch (NotLeaderException)
                {
                    Volatile.Write(ref readyLease, null);
                    logger.LogInformation("Schedule firing paused after losing leadership.");
                }
                catch (InvalidOperationException) when (!versions.IsReady)
                {
                    Volatile.Write(ref readyLease, null);
                    logger.LogWarning("Schedule firing paused while workflow versions resynchronize.");
                    await Task.Delay(TimeSpan.FromSeconds(1), time, stoppingToken);
                }
                catch (Exception error) when (error is not InvalidDataException &&
                    error is NpgsqlException { IsTransient: true } or TimeoutException or IOException)
                {
                    Volatile.Write(ref readyLease, null);
                    logger.LogError(error, "Schedule firing will reconnect and reload durable cursors.");
                    await Task.Delay(TimeSpan.FromSeconds(1), time, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { Volatile.Write(ref readyLease, null); }
    }

    private async Task ReloadAsync(CancellationToken cancellationToken)
    {
        var snapshot = new List<ScheduleTimer>();
        await using var query = source.CreateCommand("""
            SELECT s.id,s.next_fire_at FROM schedules s JOIN workflows w ON w.id=s.workflow_id
            WHERE NOT s.paused AND s.deleted_at IS NULL AND s.next_fire_at IS NOT NULL AND w.deleted_at IS NULL
            """);
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            snapshot.Add(new(reader.GetGuid(0), reader.GetFieldValue<DateTimeOffset>(1)));
        timers.Reset(snapshot);
    }
}
