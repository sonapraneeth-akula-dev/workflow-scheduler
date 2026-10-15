using System.Collections.Immutable;

namespace Wf.ControlPlane.Engine.Agents;

public enum AgentStatus : short { Healthy, Unhealthy, Draining }
public enum AgentOs : short { Windows, Linux }
public enum AgentArchitecture : short { X64, Arm64 }

public sealed record AgentRecord(
    Guid Id, string Name, AgentOs Os, AgentArchitecture Architecture, int Cores, int RamMb,
    int MaxTasks, ImmutableDictionary<string, string> Labels, string Pool,
    AgentStatus Status, DateTimeOffset LastHeartbeatAt, string Version, Guid SessionId,
    double CpuPercent = 0, long RamUsedMb = 0, long DiskFreeMb = 0, int SlotsUsed = 0)
{
    public bool IsDispatchable(DateTimeOffset now) =>
        Status == AgentStatus.Healthy && now - LastHeartbeatAt < AgentRegistry.HeartbeatTimeout;
}

public readonly record struct AgentSession(Guid AgentId, Guid SessionId);
