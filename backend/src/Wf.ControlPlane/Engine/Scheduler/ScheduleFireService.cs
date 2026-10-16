using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Wf.Contracts.Runs;
using Wf.Contracts.Schedules;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Agents;
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
    ActiveVersionCache versions, ScheduleCatchUp catchUp, TimeProvider time, ILogger<ScheduleFireService> logger) : BackgroundService
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
        ScheduleCatchUpPolicy catchUpPolicy;
        DateTimeOffset fireAt;
        await using (var read = new NpgsqlCommand("""
            SELECT s.workflow_id,s.timezone,s.cron,s.interval_s,s.variables::text,s.version_pin,
                s.overlap_policy,s.next_fire_at,r.id,s.catchup_policy
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
            catchUpPolicy = (ScheduleCatchUpPolicy)reader.GetInt16(9);
        }
        if (fireAt != timer.FireAt || fireAt > time.GetUtcNow()) return new(timer.ScheduleId, fireAt);
        var plan = catchUp.Plan(fireAt, ScheduleExpression.Create(cron, interval, timezone), catchUpPolicy, cancellationToken);
        var claimed = new HashSet<DateTimeOffset>();
        foreach (var fire in plan.CatchUp.Fires)
        {
            await using var claim = new NpgsqlCommand("""
                INSERT INTO schedule_fires(schedule_id,fire_at,outcome,reason,config_version_id)
                SELECT id,$2,$3,$4,config_version_id FROM schedules WHERE id=$1
                ON CONFLICT(schedule_id,fire_at) DO NOTHING RETURNING schedule_id
                """, connection, transaction);
            claim.Parameters.AddWithValue(timer.ScheduleId);
            claim.Parameters.AddWithValue(fire.FireAt);
            claim.Parameters.AddWithValue((short)fire.Outcome);
            claim.Parameters.AddWithValue(NpgsqlDbType.Text,
                fire.Outcome == ScheduleFireOutcome.Missed ? "missed-grace" : DBNull.Value);
            if (await claim.ExecuteScalarAsync(cancellationToken) is Guid) claimed.Add(fire.FireAt);
        }
        // Preserve every missed row; the catch-up run belongs to its newest missed source, not a second fire.
        if (plan.CatchUp.CatchUp is { } action && claimed.Contains(action.SourceFireAt))
        {
            var run = await TryStartRunAsync(connection, timer.ScheduleId, series, workflow, pin, variables, overlap,
                RunTrigger.CatchUp, cancellationToken);
            await RunStore.ExecuteAsync(connection, """
                UPDATE schedule_fires SET run_id=$3,reason=$4 WHERE schedule_id=$1 AND fire_at=$2
                """, cancellationToken, timer.ScheduleId, action.SourceFireAt, (object?)run ?? DBNull.Value,
                run is null ? "overlap" : "catch-up");
        }
        foreach (var fire in plan.CatchUp.Fires.Where(fire => fire.Outcome == ScheduleFireOutcome.Started && claimed.Contains(fire.FireAt)))
        {
            var run = await TryStartRunAsync(connection, timer.ScheduleId, series, workflow, pin, variables, overlap,
                RunTrigger.Schedule, cancellationToken);
            await RunStore.ExecuteAsync(connection, """
                UPDATE schedule_fires SET outcome=$3,run_id=$4,reason=$5 WHERE schedule_id=$1 AND fire_at=$2
                """, cancellationToken, timer.ScheduleId, fire.FireAt,
                (short)(run is null ? ScheduleFireOutcome.Skipped : ScheduleFireOutcome.Started),
                (object?)run ?? DBNull.Value, run is null ? "overlap" : DBNull.Value);
        }
        await using (var advance = new NpgsqlCommand("UPDATE schedules SET next_fire_at=$2 WHERE id=$1", connection, transaction))
        {
            advance.Parameters.AddWithValue(timer.ScheduleId);
            advance.Parameters.AddWithValue(NpgsqlDbType.TimestampTz, (object?)plan.NextFireAt ?? DBNull.Value);
            await advance.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return plan.NextFireAt is { } at ? new(timer.ScheduleId, at) : null;
    }

    private async Task<Guid?> TryStartRunAsync(NpgsqlConnection connection, Guid scheduleId, Guid series,
        string workflow, string? pin, string variables, ScheduleOverlapPolicy overlap, RunTrigger trigger,
        CancellationToken cancellationToken)
    {
        if (overlap != ScheduleOverlapPolicy.Concurrent)
        {
            await using var count = new NpgsqlCommand("SELECT count(*)::int FROM runs WHERE series_id=$1 AND status IN (0,1,2)", connection);
            count.Parameters.AddWithValue(series);
            var active = (int)(await count.ExecuteScalarAsync(cancellationToken)
                ?? throw new InvalidDataException("The overlap query returned no count."));
            if (OverlapPolicy.Decide(active, overlap) == ScheduleOverlapDecision.Skip) return null;
        }
        SelectedWorkflowVersion selected;
        try
        {
            selected = await WorkflowVersionResolver.ResolveAsync(versions, source, workflow, pin, true,
                cancellationToken, connection);
        }
        catch (Exception error) when (error is KeyNotFoundException or ArgumentException or FormatException)
        {
            throw new ScheduleFireRejectedException($"Schedule '{scheduleId}' has no valid matching workflow version.", error);
        }
        var values = JsonSerializer.Deserialize(variables, ScheduleFileJsonContext.Default.ImmutableDictionaryStringJsonElement)
            ?? throw new InvalidDataException("Schedule variables are null.");
        var bound = VariableBinder.Resolve(selected.Manifest.Variables, values, path: "$.schedule.variables",
            cancellationToken: cancellationToken);
        if (!bound.IsValid)
            throw new ScheduleFireRejectedException($"Schedule '{scheduleId}' variables failed validation: " +
                string.Join("; ", bound.Errors.Select(error => $"{error.RuleId} {error.Path}: {error.Message}")));
        var selectors = await MachineSelectorInventory.ValidateAsync(connection, null, selected.Manifest,
            VariableBinder.Bind(selected.Manifest.Variables, bound.Values), cancellationToken);
        if (!selectors.IsEmpty)
            throw new ScheduleFireRejectedException($"Schedule '{scheduleId}' selectors failed validation: " +
                string.Join("; ", selectors.Select(error => $"{error.RuleId} {error.Path}: {error.Hint}")));
        var now = time.GetUtcNow();
        var run = new RunState(Guid.CreateVersion7(now), selected.Manifest.Nodes, selected.Manifest.Metadata,
            message => logger.LogWarning("{RunStateError}", message), bound.Values);
        await RunStore.CreateAsync(connection, run, workflow, selected.VersionId, now.Add(selected.Manifest.RunTimeout),
            now, "scheduler", cancellationToken, series, trigger);
        return run.Snapshot.RunId;
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
                            var due = new List<ScheduleTimer>();
                            var limit = timers.Count;
                            for (var index = 0; index < limit && timers.TryTakeDue(time.GetUtcNow(), out var timer); index++)
                                due.Add(timer!);
                            var following = new System.Collections.Concurrent.ConcurrentBag<ScheduleTimer>();
                            await Parallel.ForEachAsync(due, new ParallelOptions
                            {
                                MaxDegreeOfParallelism = 16, CancellationToken = stoppingToken
                            }, async (timer, cancellationToken) =>
                            {
                                try
                                {
                                    if (await FireAsync(timer, cancellationToken) is { } next) following.Add(next);
                                }
                                catch (ScheduleFireRejectedException error)
                                {
                                    logger.LogError(error, "Schedule {ScheduleId} cannot fire; its cursor is retained until configuration reload.",
                                        timer.ScheduleId);
                                }
                            });
                            foreach (var next in following) timers.Add(next);
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
