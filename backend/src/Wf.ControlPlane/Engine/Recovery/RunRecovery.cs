using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using Wf.Contracts;
using Wf.Contracts.Events;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Leadership;
using Wf.Engine.Core;
using Wf.Engine.Core.Abort;
using Wf.Engine.Core.StateMachine;
using Wf.Engine.Core.Timers;

namespace Wf.ControlPlane.Engine.Recovery;

public sealed record RecoveredRun(RunState State, DeadlineTracker Timers);

public sealed class RunRecovery(NpgsqlDataSource dataSource, RunStore runs, LeaseManager leadership,
    TimeProvider time, ILogger<RunRecovery> logger) : BackgroundService
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private RecoveryState state = new(null, ImmutableDictionary<Guid, RecoveredRun>.Empty);
    public bool IsReady => Volatile.Read(ref state).Lease is { } lease && leadership.Current == lease;
    public ImmutableDictionary<Guid, RecoveredRun> RecoveredRuns =>
        IsReady ? Volatile.Read(ref state).Runs : ImmutableDictionary<Guid, RecoveredRun>.Empty;

    public Task RecoverAsync(CancellationToken cancellationToken = default) => RecoverAsync(false, cancellationToken);

    private async Task RecoverAsync(bool incremental, CancellationToken cancellationToken)
    {
        var lease = leadership.RequireLease();
        await gate.WaitAsync(cancellationToken);
        try
        {
            var previous = Volatile.Read(ref state);
            var recovered = incremental && previous.Lease == lease ? previous.Runs.ToBuilder() :
                ImmutableDictionary.CreateBuilder<Guid, RecoveredRun>();
            if (!incremental || previous.Lease != lease)
                Volatile.Write(ref state, new(null, ImmutableDictionary<Guid, RecoveredRun>.Empty));
            var changed = await ProcessCommandsAsync(lease, cancellationToken);
            foreach (var id in changed) recovered.Remove(id);
            var ids = new List<Guid>();
            await using (var command = dataSource.CreateCommand("""
                SELECT id FROM runs WHERE status IN (0,1,2) AND NOT(id=ANY($1)) ORDER BY id
                """))
            {
                command.Parameters.AddWithValue(recovered.Keys.ToArray());
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken)) ids.Add(reader.GetGuid(0));
            }
            var loaded = new System.Collections.Concurrent.ConcurrentDictionary<Guid, RecoveredRun>();
            await Parallel.ForEachAsync(ids.Chunk(64), new ParallelOptions
            {
                MaxDegreeOfParallelism = 8, CancellationToken = cancellationToken
            }, async (batch, token) =>
            {
                await using var connection = await dataSource.OpenConnectionAsync(token);
                await using var transaction = await connection.BeginTransactionAsync(token);
                await leadership.FenceAsync(connection, transaction, lease, token);
                var states = await runs.LoadManyAsync(connection, batch, token);
                var timers = await LoadTimersAsync(connection, states, token);
                await transaction.CommitAsync(token);
                foreach (var (id, run) in states)
                    if (!TransitionTable.IsTerminal(run.Snapshot.Status))
                        loaded.TryAdd(id, new(run, new(new RecoveryClock(time), restored: timers[id])));
            });
            foreach (var (id, run) in loaded) recovered[id] = run;
            await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken))
            await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
            {
                await leadership.FenceAsync(connection, transaction, lease, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            if (leadership.Current != lease) throw new NotLeaderException(leadership.LeaderHint);
            Volatile.Write(ref state, new(lease, recovered.ToImmutable()));
            logger.LogInformation("Recovered {RunCount} active runs for leader epoch {Epoch}.", recovered.Count, lease.Epoch);
        }
        finally { gate.Release(); }
    }

    private async Task<HashSet<Guid>> ProcessCommandsAsync(LeaseToken lease, CancellationToken cancellationToken)
    {
        var changed = new HashSet<Guid>();
        while (true)
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await leadership.FenceAsync(connection, transaction, lease, cancellationToken);
            Guid id, runId;
            short kind;
            string payload, createdBy;
            DateTimeOffset createdAt;
            await using (var command = new NpgsqlCommand("""
                SELECT id,kind,run_id,payload::text,created_by,created_at FROM commands
                WHERE processed_at IS NULL ORDER BY created_at,kind,id LIMIT 1 FOR UPDATE
                """, connection, transaction))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                if (!await reader.ReadAsync(cancellationToken))
                {
                    await reader.DisposeAsync();
                    await transaction.CommitAsync(cancellationToken);
                    return changed;
                }
                id = reader.GetGuid(0);
                kind = reader.GetInt16(1);
                runId = reader.GetGuid(2);
                payload = reader.GetString(3);
                createdBy = reader.GetString(4);
                createdAt = reader.GetFieldValue<DateTimeOffset>(5);
            }
            CommittedRunTelemetry? telemetry = null;
            if (kind == 0)
            {
                var trigger = CommandInbox.ReadInitial(payload);
                if (trigger.InitialState.RunId != runId || trigger.DeadlineAt <= createdAt)
                    throw new InvalidDataException($"Trigger command '{id}' has a mismatched run or invalid deadline.");
                if (trigger.ParentRunId is not null)
                    await RerunHandler.ApplyAsync(connection, trigger, createdAt, createdBy, cancellationToken);
                else
                {
                    var run = RunState.Restore(trigger.InitialState, [], message => logger.LogWarning("{RunStateError}", message));
                    await RunStore.CreateAsync(connection, run, trigger.WorkflowId, trigger.VersionId,
                        trigger.DeadlineAt, createdAt, createdBy, cancellationToken, trigger.SeriesId,
                        triggeredByName: trigger.TriggeredByName);
                }
            }
            else if (kind == 1)
            {
                var abort = JsonSerializer.Deserialize(payload, RecoveryJson.Default.AbortRecord)
                    ?? throw new InvalidDataException($"Abort command '{id}' has no payload.");
                // PostgreSQL timestamps retain microseconds; the JSON acceptance record retains .NET ticks.
                if (abort.By != createdBy || abort.At.Ticks / 10 > createdAt.Ticks / 10)
                    throw new InvalidDataException($"Abort command '{id}' has an invalid acceptance identity or timestamp.");
                var run = await runs.LoadAsync(connection, runId, true, cancellationToken);
                if (!TransitionTable.IsTerminal(run.Snapshot.Status))
                {
                    var plan = AbortHandler.Request(run.Snapshot, abort);
                    var at = time.GetUtcNow();
                    foreach (var change in plan.Tasks)
                        await RunStore.AppendAsync(connection, run,
                            run.CreateEvent(EventEntity.Task, change.Task, (int)change.Status, at), cancellationToken);
                    foreach (var change in plan.Nodes)
                        await RunStore.AppendAsync(connection, run,
                            run.CreateEvent(EventEntity.Node, change.Node, (int)change.Status, at), cancellationToken);
                    if (plan.Status is { } status)
                        await RunStore.AppendAsync(connection, run,
                            run.CreateEvent(EventEntity.Run, null, (int)status, at, plan.Record), cancellationToken);
                    telemetry = await CommittedRunTelemetry.CaptureAsync(connection, run.Snapshot, cancellationToken);
                }
            }
            else throw new InvalidDataException($"Command '{id}' has unsupported kind '{kind}'.");
            await RunStore.ExecuteAsync(connection, "UPDATE commands SET processed_at=GREATEST(created_at,clock_timestamp()) WHERE id=$1",
                cancellationToken, id);
            await transaction.CommitAsync(cancellationToken);
            telemetry?.Emit();
            changed.Add(runId);
        }
    }

    private static async Task<Dictionary<Guid, List<TimerEntry>>> LoadTimersAsync(NpgsqlConnection connection,
        Dictionary<Guid, RunState> states,
        CancellationToken cancellationToken)
    {
        var ids = states.Keys.ToArray();
        var timers = ids.ToDictionary(id => id, _ => new List<TimerEntry>());
        await using (var command = new NpgsqlCommand("""
            SELECT id,deadline_at FROM runs
            WHERE run_day=ANY(SELECT uuid_v7_run_day(id) FROM unnest($1::uuid[]) id) AND id=ANY($1)
            """, connection))
        {
            command.Parameters.AddWithValue(ids);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetGuid(0);
                timers[id].Add(new(new(TimerScope.Run, id.ToString("D")), DeadlineAt: reader.GetFieldValue<DateTimeOffset>(1)));
            }
        }
        await using (var command = new NpgsqlCommand("""
            SELECT run_id,node_key,deadline_at FROM node_runs
            WHERE run_day=ANY(SELECT uuid_v7_run_day(id) FROM unnest($1::uuid[]) id)
                AND run_id=ANY($1) AND deadline_at IS NOT NULL
            """, connection))
        {
            command.Parameters.AddWithValue(ids);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                if (states[reader.GetGuid(0)].Snapshot.Nodes.TryGetValue(reader.GetString(1), out var status) && !TransitionTable.IsTerminal(status))
                    timers[reader.GetGuid(0)].Add(new(new(TimerScope.Node, reader.GetString(1)),
                        DeadlineAt: reader.GetFieldValue<DateTimeOffset>(2)));
        }
        await using (var command = new NpgsqlCommand("""
            SELECT DISTINCT ON(run_id,task_key) run_id,task_key,attempt,deadline_at,retry_at,kill_at,timer_phase
            FROM task_attempts WHERE run_day=ANY(SELECT uuid_v7_run_day(id) FROM unnest($1::uuid[]) id)
                AND run_id=ANY($1) ORDER BY run_id,task_key,attempt DESC
            """, connection))
        {
            command.Parameters.AddWithValue(ids);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetGuid(0);
                var snapshot = states[id].Snapshot;
                var key = reader.GetString(1);
                if (!snapshot.Tasks.TryGetValue(key, out var status) || TransitionTable.IsTerminal(status)) continue;
                var phase = (TimerPhase)reader.GetInt16(6);
                if (!reader.IsDBNull(4) && status == ExecutionStatus.Retrying &&
                    phase is TimerPhase.Waiting or TimerPhase.RetryDispatched)
                    timers[id].Add(new(new(TimerScope.Retry, key), RetryAt: reader.GetFieldValue<DateTimeOffset>(4), Phase: phase));
                else if (status is ExecutionStatus.Dispatched or ExecutionStatus.Running)
                    timers[id].Add(new(new(TimerScope.Attempt, $"{key}/{reader.GetInt16(2)}"),
                        DeadlineAt: reader.GetFieldValue<DateTimeOffset>(3),
                        KillAt: reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5), Phase: phase));
            }
        }
        return timers;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100), time);
        try
        {
            do
            {
                try
                {
                    if (leadership.Current is null) continue;
                    if (!IsReady) await RecoverAsync(stoppingToken);
                    else
                    {
                        await using var pending = dataSource.CreateCommand("""
                            SELECT EXISTS(SELECT 1 FROM commands WHERE processed_at IS NULL)
                                OR EXISTS(SELECT 1 FROM runs WHERE status IN (0,1,2) AND NOT (id=ANY($1)))
                            """);
                        pending.Parameters.AddWithValue(Volatile.Read(ref state).Runs.Keys.ToArray());
                        if ((bool)(await pending.ExecuteScalarAsync(stoppingToken) ?? false))
                            await RecoverAsync(true, stoppingToken);
                    }
                }
                catch (NotLeaderException) { logger.LogInformation("Run recovery paused after losing leadership."); }
                catch (Exception exception) when (exception is NpgsqlException { IsTransient: true } or
                    PostgresException { SqlState: "25P04" } or TimeoutException or IOException)
                {
                    Volatile.Write(ref state, new(null, ImmutableDictionary<Guid, RecoveredRun>.Empty));
                    logger.LogError(exception, "Run recovery will retry when durable storage is available.");
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public override void Dispose()
    {
        base.Dispose();
        gate.Dispose();
    }

    private sealed record RecoveryState(LeaseToken? Lease, ImmutableDictionary<Guid, RecoveredRun> Runs);
    private sealed class RecoveryClock(TimeProvider time) : IClock { public DateTimeOffset UtcNow => time.GetUtcNow(); }
}
