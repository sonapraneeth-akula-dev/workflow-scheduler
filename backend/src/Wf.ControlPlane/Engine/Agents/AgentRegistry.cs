using System.Collections.Concurrent;
using System.Collections.Immutable;
using Npgsql;
using Wf.Contracts.Agent.V1;
using Wf.ControlPlane.Engine.Leadership;
using Wf.Engine.Core.Agents;

namespace Wf.ControlPlane.Engine.Agents;

public sealed class AgentRegistry(NpgsqlDataSource dataSource, TimeProvider timeProvider, LeaseManager? leadership = null) : IDisposable
{
    public static readonly TimeSpan HeartbeatTimeout = AgentLossPolicy.UnhealthyAfter;
    public const int MaximumHelloAttempts = 8192;
    public const int MaximumHelloMessageBytes = 1024 * 1024;
    private readonly ConcurrentDictionary<Guid, AgentRecord> agents = new();
    private readonly SemaphoreSlim writes = new(1, 1);

    public AgentRecord? Find(Guid id) => agents.GetValueOrDefault(id);

    public IReadOnlyList<AgentRecord> DispatchableAgents() =>
        agents.Values.Where(agent => agent.IsDispatchable(timeProvider.GetUtcNow())).ToArray();

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        await writes.WaitAsync(cancellationToken);
        try
        {
            var loaded = new List<AgentRecord>();
            await using var command = dataSource.CreateCommand("""
                SELECT id,name,os,arch,cores,ram_mb,max_tasks,labels,pool,status,last_heartbeat_at,agent_version,
                       session_id,cpu_percent,ram_used_mb,disk_free_mb,slots_used FROM agents
                """);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var labels = ParseLabels(reader.GetFieldValue<string[]>(7));
                loaded.Add(new AgentRecord(reader.GetGuid(0), reader.GetString(1), (AgentOs)reader.GetInt16(2),
                    (AgentArchitecture)reader.GetInt16(3), reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6),
                    labels, reader.GetString(8), (AgentStatus)reader.GetInt16(9), reader.GetFieldValue<DateTimeOffset>(10),
                    reader.GetString(11), reader.GetGuid(12), reader.GetDouble(13), reader.GetInt64(14),
                    reader.GetInt64(15), reader.GetInt32(16)));
            }
            agents.Clear();
            foreach (var agent in loaded) agents[agent.Id] = agent;
        }
        finally { writes.Release(); }
    }

    public async Task<AgentSession> RegisterAsync(Hello hello, CancellationToken cancellationToken)
    {
        var agent = ValidateHello(hello, timeProvider.GetUtcNow());
        var lease = leadership?.RequireLease();
        await writes.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            if (leadership is not null) await leadership.FenceAsync(connection, transaction, lease!, cancellationToken);
            await using var command = new NpgsqlCommand("""
                INSERT INTO agents
                    (id,name,os,arch,cores,ram_mb,max_tasks,labels,pool,status,last_heartbeat_at,agent_version,session_id,slots_used,
                        supports_calibration_commands)
                VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,0,$10,$11,$12,$13,$14)
                ON CONFLICT (id) DO UPDATE SET
                    name=EXCLUDED.name,os=EXCLUDED.os,arch=EXCLUDED.arch,cores=EXCLUDED.cores,
                    ram_mb=EXCLUDED.ram_mb,max_tasks=EXCLUDED.max_tasks,
                    labels=COALESCE(agents.labels_override,EXCLUDED.labels),pool=COALESCE(agents.pool_override,EXCLUDED.pool),
                    status=CASE WHEN agents.status=2 THEN 2 ELSE 0 END,
                    last_heartbeat_at=EXCLUDED.last_heartbeat_at,agent_version=EXCLUDED.agent_version,
                    session_id=EXCLUDED.session_id,slots_used=EXCLUDED.slots_used,cpu_percent=0,ram_used_mb=0,disk_free_mb=0,
                    supports_calibration_commands=EXCLUDED.supports_calibration_commands
                RETURNING status,labels,pool
                """, connection, transaction);
            command.Parameters.AddWithValue(agent.Id);
            command.Parameters.AddWithValue(agent.Name);
            command.Parameters.AddWithValue((short)agent.Os);
            command.Parameters.AddWithValue((short)agent.Architecture);
            command.Parameters.AddWithValue(agent.Cores);
            command.Parameters.AddWithValue(agent.RamMb);
            command.Parameters.AddWithValue(agent.MaxTasks);
            command.Parameters.AddWithValue(agent.Labels.Select(pair => $"{pair.Key}={pair.Value}").ToArray());
            command.Parameters.AddWithValue(agent.Pool);
            command.Parameters.AddWithValue(agent.LastHeartbeatAt);
            command.Parameters.AddWithValue(agent.Version);
            command.Parameters.AddWithValue(agent.SessionId);
            command.Parameters.AddWithValue(agent.SlotsUsed);
            command.Parameters.AddWithValue(hello.SupportsCalibrationCommands);
            AgentStatus status;
            ImmutableDictionary<string, string> labels;
            string pool;
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                if (!await reader.ReadAsync(cancellationToken)) throw new InvalidDataException("Agent registration returned no row.");
                status = (AgentStatus)reader.GetInt16(0);
                labels = ParseLabels(reader.GetFieldValue<string[]>(1));
                pool = reader.GetString(2);
            }
            await transaction.CommitAsync(cancellationToken);
            agents[agent.Id] = agent with { Status = status, Labels = labels, Pool = pool };
            return new AgentSession(agent.Id, agent.SessionId);
        }
        finally { writes.Release(); }
    }

    public async Task<bool> HeartbeatAsync(AgentSession session, Heartbeat heartbeat, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(heartbeat);
        var lease = leadership?.RequireLease();
        await writes.WaitAsync(cancellationToken);
        try
        {
            if (!agents.TryGetValue(session.AgentId, out var agent) || agent.SessionId != session.SessionId) return false;
            if (!double.IsFinite(heartbeat.CpuPercent) || heartbeat.CpuPercent is < 0 or > 100 ||
                heartbeat.RamUsedMb > (ulong)agent.RamMb || heartbeat.DiskFreeMb > long.MaxValue ||
                heartbeat.SlotsUsed > agent.MaxTasks || heartbeat.At is null)
                throw new ArgumentException("Heartbeat contains invalid resource metrics.", nameof(heartbeat));
            try { _ = heartbeat.At.ToDateTimeOffset(); }
            catch (InvalidOperationException)
            {
                throw new ArgumentException("Heartbeat contains an invalid timestamp.", nameof(heartbeat));
            }
            var now = timeProvider.GetUtcNow();
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            if (leadership is not null) await leadership.FenceAsync(connection, transaction, lease!, cancellationToken);
            await using var command = new NpgsqlCommand("""
                UPDATE agents SET last_heartbeat_at=$3,status=CASE WHEN status=1 THEN 0 ELSE status END,
                    cpu_percent=$4,ram_used_mb=$5,disk_free_mb=$6,slots_used=$7
                WHERE id=$1 AND session_id=$2 RETURNING status
                """, connection, transaction);
            command.Parameters.AddWithValue(session.AgentId);
            command.Parameters.AddWithValue(session.SessionId);
            command.Parameters.AddWithValue(now);
            command.Parameters.AddWithValue(heartbeat.CpuPercent);
            command.Parameters.AddWithValue((long)heartbeat.RamUsedMb);
            command.Parameters.AddWithValue((long)heartbeat.DiskFreeMb);
            command.Parameters.AddWithValue((int)heartbeat.SlotsUsed);
            if (await command.ExecuteScalarAsync(cancellationToken) is not short status) return false;
            await transaction.CommitAsync(cancellationToken);
            agents[agent.Id] = agent with
            {
                LastHeartbeatAt = now, Status = (AgentStatus)status,
                CpuPercent = heartbeat.CpuPercent, RamUsedMb = (long)heartbeat.RamUsedMb,
                DiskFreeMb = (long)heartbeat.DiskFreeMb, SlotsUsed = (int)heartbeat.SlotsUsed
            };
            return true;
        }
        finally { writes.Release(); }
    }

    public async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        var lease = leadership?.RequireLease();
        await writes.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            if (leadership is not null) await leadership.FenceAsync(connection, transaction, lease!, cancellationToken);
            await using var command = new NpgsqlCommand("""
                UPDATE agents SET status=1 WHERE status=0 AND last_heartbeat_at <= $1 RETURNING id
                """, connection, transaction);
            command.Parameters.AddWithValue(timeProvider.GetUtcNow() - HeartbeatTimeout);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var expired = new List<Guid>();
            while (await reader.ReadAsync(cancellationToken)) expired.Add(reader.GetGuid(0));
            await reader.DisposeAsync();
            await transaction.CommitAsync(cancellationToken);
            foreach (var id in expired)
                if (agents.TryGetValue(id, out var agent)) agents[id] = agent with { Status = AgentStatus.Unhealthy };
            return expired.Count;
        }
        finally { writes.Release(); }
    }

    public static AgentRecord ValidateHello(Hello hello, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(hello);
        if (!Guid.TryParse(hello.Id, out var id) || id == Guid.Empty)
            throw new ArgumentException("Hello.id must be a non-empty UUID.", nameof(hello));
        if (hello.ProtocolVersion != 1) throw new ArgumentException("Unsupported agent protocol version.", nameof(hello));
        if (string.IsNullOrWhiteSpace(hello.Name) || hello.Name.Length > 200 ||
            string.IsNullOrWhiteSpace(hello.Pool) || hello.Pool.Length > 200 ||
            string.IsNullOrWhiteSpace(hello.Version) || hello.Version.Length > 100)
            throw new ArgumentException("Hello name, pool and version are required and must fit their bounds.", nameof(hello));
        if (hello.Cores is 0 or > int.MaxValue || hello.RamMb is 0 or > int.MaxValue ||
            hello.MaxTasks is 0 or > int.MaxValue)
            throw new ArgumentException("Hello contains invalid machine capacity.", nameof(hello));
        if (hello.Inflight.Count > MaximumHelloAttempts ||
            new AgentMessage { Sequence = ulong.MaxValue, Hello = hello }.CalculateSize() > MaximumHelloMessageBytes)
            throw new ArgumentException("Hello attempt inventory exceeds the protocol count or message-size limit.", nameof(hello));
        var os = hello.Os switch
        {
            "windows" => AgentOs.Windows, "linux" => AgentOs.Linux,
            _ => throw new ArgumentException("Hello.os must be windows or linux.", nameof(hello))
        };
        var arch = hello.Arch switch
        {
            "x64" => AgentArchitecture.X64, "arm64" => AgentArchitecture.Arm64,
            _ => throw new ArgumentException("Hello.arch must be x64 or arm64.", nameof(hello))
        };
        ValidateMetadata(hello.Labels, hello.Pool);
        var inflight = new HashSet<string>(StringComparer.Ordinal);
        foreach (var attempt in hello.Inflight)
        {
            if (!Guid.TryParse(attempt.RunId, out var run) || run == Guid.Empty || attempt.Attempt is 0 or > 32767 ||
                string.IsNullOrWhiteSpace(attempt.NodeKey) || string.IsNullOrWhiteSpace(attempt.TaskKey) ||
                attempt.NodeKey.Contains('/') || attempt.TaskKey.Contains('/') ||
                !inflight.Add($"{run}/{attempt.NodeKey}/{attempt.TaskKey}/{attempt.Attempt}"))
                throw new ArgumentException("Hello contains an invalid or duplicate in-flight attempt.", nameof(hello));
        }
        return new AgentRecord(id, hello.Name, os, arch, (int)hello.Cores, (int)hello.RamMb, (int)hello.MaxTasks,
            hello.Labels.ToImmutableDictionary(StringComparer.Ordinal), hello.Pool, AgentStatus.Healthy, now, hello.Version,
            // Unacknowledged completions remain in the logical inventory but no longer occupy physical slots.
            Guid.NewGuid(), SlotsUsed: Math.Min((int)hello.MaxTasks, hello.Inflight.Count));
    }

    public static void ValidateMetadata(IReadOnlyDictionary<string, string> labels, string pool)
    {
        ArgumentNullException.ThrowIfNull(labels);
        if (string.IsNullOrWhiteSpace(pool) || pool.Length > 200 || pool.Any(char.IsControl) ||
            labels.Count > 100 || labels.Any(pair => string.IsNullOrWhiteSpace(pair.Key) ||
                pair.Key.Length > 100 || pair.Key.Contains('=') || pair.Key.Any(char.IsControl) ||
                string.IsNullOrWhiteSpace(pair.Value) || pair.Value.Length > 200 || pair.Value.Any(char.IsControl)))
            throw new ArgumentException("Machine pool or labels do not match the supported metadata bounds.");
    }

    public static ImmutableDictionary<string, string> ParseLabels(IEnumerable<string> labels) =>
        labels.Select(label => label.Split('=', 2))
            .ToImmutableDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);

    public void Dispose() => writes.Dispose();
}
