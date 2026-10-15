using Google.Protobuf;
using Npgsql;
using Wf.Contracts;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Events;
using Wf.ControlPlane.Engine.Agents;
using Wf.Engine.Core;
using Wf.Engine.Core.Placement;
using DispatchCommand = Wf.Contracts.Agent.V1.Dispatch;

namespace Wf.ControlPlane.Engine.Dispatch;

public sealed class Dispatcher(NpgsqlDataSource dataSource, RunStore runs, AgentRegistry agents,
    PlacementEngine placement, TimeProvider timeProvider) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<PlacementDecision> DispatchAsync(RunState run, PlacementRequest request, DispatchCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(command);
        var key = $"{request.NodeKey}/{request.TaskKey}";
        if (request.RunId != run.Snapshot.RunId || command.Task is null ||
            command.Task.RunId != request.RunId.ToString("D") || command.Task.NodeKey != request.NodeKey ||
            command.Task.TaskKey != request.TaskKey || command.Task.Attempt != request.Attempt ||
            request.Attempt > short.MaxValue || command.Sha256 != request.PackageSha256 ||
            command.DeadlineAt is null || command.DeadlineAt.ToDateTimeOffset() <= timeProvider.GetUtcNow() ||
            string.IsNullOrWhiteSpace(command.Version) || !Uri.TryCreate(command.PackageUri, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("https" or "http") || uri.Scheme == "http" && !uri.IsLoopback)
            throw new ArgumentException("Dispatch does not match its placement request or has invalid package/deadline data.", nameof(command));
        var lease = runs.CaptureLease();
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await runs.FenceAsync(connection, transaction, lease, cancellationToken);
            await RunStore.ExecuteAsync(connection, "SELECT pg_advisory_xact_lock(746312081)", cancellationToken);
            var durable = await runs.LoadAsync(connection, request.RunId, true, cancellationToken);
            if (durable.Snapshot.LastSequence != run.Snapshot.LastSequence ||
                !durable.Snapshot.Tasks.TryGetValue(key, out var state) || state != ExecutionStatus.Ready ||
                durable.Snapshot.Status != RunStatus.Running)
                throw new InvalidOperationException("Only a committed Ready task in a running run can be dispatched.");
            var capacity = agents.DispatchableAgents().Select(agent => agent.Capacity(timeProvider.GetUtcNow())).ToDictionary(agent => agent.Id);
            await using (var pending = new NpgsqlCommand("""
                SELECT agent_id,count(*),sum(required_ram_mb) FROM task_attempts WHERE status IN (2,3) GROUP BY agent_id
                """, connection))
            await using (var reader = await pending.ExecuteReaderAsync(cancellationToken))
                while (await reader.ReadAsync(cancellationToken))
                    if (!reader.IsDBNull(0) && capacity.TryGetValue(reader.GetGuid(0), out var agent))
                        capacity[agent.Id] = agent with
                        {
                            SlotsUsed = Math.Min(agent.MaxTasks, Math.Max(agent.SlotsUsed, checked((int)reader.GetInt64(1)))),
                            AvailableRamMb = (long)Math.Max(0m, agent.AvailableRamMb - reader.GetFieldValue<decimal>(2))
                        };
            var decision = placement.Propose(request, capacity.Values.ToArray());
            if (decision.Agent is null) return decision;
            var dispatch = command.Clone();
            dispatch.ReservationEpoch = 1;
            var change = durable.CreateEvent(EventEntity.Task, key, (int)ExecutionStatus.Dispatched, timeProvider.GetUtcNow());
            await RunStore.AppendAsync(connection, durable, change, cancellationToken);
            await RunStore.ExecuteAsync(connection, """
                WITH attempt AS (
                    INSERT INTO task_attempts(run_day,run_id,task_key,attempt,agent_id,status,queued_at,deadline_at,required_ram_mb)
                    VALUES(uuid_v7_run_day($1),$1,$2,$3,$4,2,$5,$6,$7)
                    RETURNING run_day,run_id,task_key,attempt,agent_id
                )
                INSERT INTO dispatch_outbox(run_day,run_id,task_key,attempt,agent_id,payload)
                SELECT run_day,run_id,task_key,attempt,agent_id,$8 FROM attempt
                """, cancellationToken, request.RunId, key, (short)request.Attempt, decision.Agent.Id,
                timeProvider.GetUtcNow(), dispatch.DeadlineAt.ToDateTimeOffset(), request.RequiredRamMb, dispatch.ToByteArray());
            await transaction.CommitAsync(cancellationToken);
            run.Apply(change);
            placement.Apply(decision);
            return decision;
        }
        finally { gate.Release(); }
    }

    public void Dispose() => gate.Dispose();
}
