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

    public async Task RecoverAsync(CancellationToken cancellationToken = default)
    {
        var lease = leadership.RequireLease();
        await gate.WaitAsync(cancellationToken);
        try
        {
            Volatile.Write(ref state, new(null, ImmutableDictionary<Guid, RecoveredRun>.Empty));
            await ProcessCommandsAsync(lease, cancellationToken);
            var ids = new List<Guid>();
            await using (var command = dataSource.CreateCommand("SELECT id FROM runs WHERE status IN (0,1,2) ORDER BY id"))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                while (await reader.ReadAsync(cancellationToken)) ids.Add(reader.GetGuid(0));
            var recovered = ImmutableDictionary.CreateBuilder<Guid, RecoveredRun>();
            foreach (var id in ids)
            {
                await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
                await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
                await leadership.FenceAsync(connection, transaction, lease, cancellationToken);
                var run = await runs.LoadAsync(connection, id, true, cancellationToken);
                if (!TransitionTable.IsTerminal(run.Snapshot.Status))
                {
                    var timers = await LoadTimersAsync(connection, run.Snapshot, cancellationToken);
                    recovered.Add(id, new(run, new(new RecoveryClock(time), restored: timers)));
                }
                await transaction.CommitAsync(cancellationToken);
            }
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

    private async Task ProcessCommandsAsync(LeaseToken lease, CancellationToken cancellationToken)
    {
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
                    return;
                }
                id = reader.GetGuid(0);
                kind = reader.GetInt16(1);
                runId = reader.GetGuid(2);
                payload = reader.GetString(3);
                createdBy = reader.GetString(4);
                createdAt = reader.GetFieldValue<DateTimeOffset>(5);
            }
            if (kind == 0)
            {
                var trigger = JsonSerializer.Deserialize(payload, RecoveryJson.Default.TriggerRunCommand)
                    ?? throw new InvalidDataException($"Trigger command '{id}' has no payload.");
                CommandInbox.Validate(trigger);
                if (trigger.InitialState.RunId != runId || trigger.DeadlineAt <= createdAt)
                    throw new InvalidDataException($"Trigger command '{id}' has a mismatched run or invalid deadline.");
                var run = RunState.Restore(trigger.InitialState, [], message => logger.LogWarning("{RunStateError}", message));
                await RunStore.CreateAsync(connection, run, trigger.WorkflowId, trigger.VersionId,
                    trigger.DeadlineAt, createdAt, createdBy, cancellationToken);
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
                }
            }
            else throw new InvalidDataException($"Command '{id}' has unsupported kind '{kind}'.");
            await RunStore.ExecuteAsync(connection, "UPDATE commands SET processed_at=GREATEST(created_at,clock_timestamp()) WHERE id=$1",
                cancellationToken, id);
            await transaction.CommitAsync(cancellationToken);
        }
    }

    private static async Task<List<TimerEntry>> LoadTimersAsync(NpgsqlConnection connection, RunSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var timers = new List<TimerEntry>();
        await using (var command = new NpgsqlCommand("SELECT deadline_at FROM runs WHERE run_day=uuid_v7_run_day($1) AND id=$1", connection))
        {
            command.Parameters.AddWithValue(snapshot.RunId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw new InvalidDataException("Recovered run has no deadline.");
            timers.Add(new(new(TimerScope.Run, snapshot.RunId.ToString("D")), DeadlineAt: reader.GetFieldValue<DateTimeOffset>(0)));
        }
        await using (var command = new NpgsqlCommand("""
            SELECT node_key,deadline_at FROM node_runs
            WHERE run_day=uuid_v7_run_day($1) AND run_id=$1 AND deadline_at IS NOT NULL
            """, connection))
        {
            command.Parameters.AddWithValue(snapshot.RunId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                if (snapshot.Nodes.TryGetValue(reader.GetString(0), out var status) && !TransitionTable.IsTerminal(status))
                    timers.Add(new(new(TimerScope.Node, reader.GetString(0)), DeadlineAt: reader.GetFieldValue<DateTimeOffset>(1)));
        }
        await using (var command = new NpgsqlCommand("""
            SELECT DISTINCT ON(task_key) task_key,attempt,deadline_at,retry_at,kill_at,timer_phase
            FROM task_attempts WHERE run_day=uuid_v7_run_day($1) AND run_id=$1 ORDER BY task_key,attempt DESC
            """, connection))
        {
            command.Parameters.AddWithValue(snapshot.RunId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var key = reader.GetString(0);
                if (!snapshot.Tasks.TryGetValue(key, out var status) || TransitionTable.IsTerminal(status)) continue;
                var phase = (TimerPhase)reader.GetInt16(5);
                if (!reader.IsDBNull(3) && status == ExecutionStatus.Retrying &&
                    phase is TimerPhase.Waiting or TimerPhase.RetryDispatched)
                    timers.Add(new(new(TimerScope.Retry, key), RetryAt: reader.GetFieldValue<DateTimeOffset>(3), Phase: phase));
                else if (status is ExecutionStatus.Dispatched or ExecutionStatus.Running)
                    timers.Add(new(new(TimerScope.Attempt, $"{key}/{reader.GetInt16(1)}"),
                        DeadlineAt: reader.GetFieldValue<DateTimeOffset>(2),
                        KillAt: reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4), Phase: phase));
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
                        await using var pending = dataSource.CreateCommand("SELECT EXISTS(SELECT 1 FROM commands WHERE processed_at IS NULL)");
                        if ((bool)(await pending.ExecuteScalarAsync(stoppingToken) ?? false))
                            await RecoverAsync(stoppingToken);
                    }
                }
                catch (NotLeaderException) { logger.LogInformation("Run recovery paused after losing leadership."); }
                catch (Exception exception) when (exception is NpgsqlException { IsTransient: true } or TimeoutException or IOException)
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
