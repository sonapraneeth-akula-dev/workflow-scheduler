using System.Collections.Immutable;
using Wf.Contracts.Definition;

namespace Wf.Engine.Core.Placement;

public sealed record PlacementAgent(Guid Id, string Name, string Os, string Pool,
    int SlotsUsed, int MaxTasks, long AvailableRamMb, bool Healthy = true, bool Draining = false,
    ImmutableDictionary<string, string>? Labels = null, ImmutableHashSet<Guid>? WarmRuns = null,
    ImmutableHashSet<string>? WarmPackages = null, Guid? ReservedForRun = null,
    string? ReservedForNode = null, long ReservationEpoch = 0);

public sealed record PlacementRequest(Guid RunId, string NodeKey, string TaskKey, int Attempt, string PackageSha256,
    PlacementPolicy? Policy = null, long RequiredRamMb = 0, bool RePlacement = false,
    Guid? PreviousAgentId = null, bool AutoRetry = true);

public enum PlacementReason { WarmHost, LeastLoaded, RoundRobin, Affinity, WaitingForMachine, WaitingForAffinity, AutoRetryDisabled }

public sealed record PlacementSnapshot(long Revision, ImmutableDictionary<Guid, Guid> RunPins,
    ImmutableDictionary<string, Guid> NodePins, Guid? LastAgentId = null)
{
    public static PlacementSnapshot Empty { get; } = new(0, ImmutableDictionary<Guid, Guid>.Empty,
        ImmutableDictionary<string, Guid>.Empty.WithComparers(StringComparer.Ordinal));
}

public sealed class PlacementDecision
{
    public PlacementRequest Request { get; }
    public PlacementAgent? Agent { get; }
    public PlacementReason Reason { get; }
    internal Guid Owner { get; }
    internal long Revision { get; }

    internal PlacementDecision(PlacementRequest request, PlacementAgent? agent, PlacementReason reason, Guid owner, long revision) =>
        (Request, Agent, Reason, Owner, Revision) = (request, agent, reason, owner, revision);
}

// Caller supplies authoritative capacity, including committed-but-not-yet-heartbeating dispatches.
public sealed class PlacementEngine
{
    private readonly Guid owner = Guid.NewGuid();
    public PlacementSnapshot Snapshot { get; private set; }

    public PlacementEngine(PlacementSnapshot? restored = null)
    {
        var snapshot = restored ?? PlacementSnapshot.Empty;
        ArgumentNullException.ThrowIfNull(snapshot.RunPins);
        ArgumentNullException.ThrowIfNull(snapshot.NodePins);
        if (snapshot.Revision < 0 || snapshot.LastAgentId == Guid.Empty ||
            snapshot.RunPins.Any(pair => pair.Key == Guid.Empty || pair.Value == Guid.Empty) ||
            snapshot.NodePins.Any(pair => pair.Value == Guid.Empty || !ValidNodePinKey(pair.Key)))
            throw new ArgumentException("Invalid placement snapshot.", nameof(restored));
        Snapshot = snapshot;
    }

    public PlacementDecision Propose(PlacementRequest request, IReadOnlyList<PlacementAgent> agents)
    {
        Validate(request);
        ArgumentNullException.ThrowIfNull(agents);
        var ids = new HashSet<Guid>();
        foreach (var agent in agents)
        {
            Validate(agent);
            if (!ids.Add(agent.Id)) throw new ArgumentException("Agent IDs must be unique.", nameof(agents));
        }
        if (request.RePlacement && !request.AutoRetry) return Decision(null, PlacementReason.AutoRetryDisabled);
        var affinity = request.Policy?.Affinity;
        Guid? pinned = affinity?.Kind switch
        {
            AffinityKind.Run => Snapshot.RunPins.GetValueOrDefault(request.RunId),
            AffinityKind.Node => Snapshot.NodePins.GetValueOrDefault(NodeKey(request.RunId, request.NodeKey)),
            AffinityKind.SameMachineAs => Snapshot.NodePins.GetValueOrDefault(NodeKey(request.RunId, affinity.NodeKey!)),
            _ => null
        };
        if (pinned == Guid.Empty) pinned = null;
        if (affinity?.Kind == AffinityKind.SameMachineAs && pinned is null)
            return Decision(null, PlacementReason.WaitingForAffinity);
        if (request.RePlacement && pinned == request.PreviousAgentId) pinned = null;

        HashSet<Guid>? cohort = null;
        if (request.Policy?.Selector is { } selector && (selector.Count is not null || !selector.MachineIds.IsEmpty))
        {
            var count = selector.Count ?? selector.MachineIds.Length;
            // Capacity changes must not expand a count-limited pool onto extra machines.
            var matching = agents.Where(Eligible).OrderBy(agent => agent.Id == pinned ? 0 : 1)
                .ThenBy(agent => agent.Id).Take(count).ToArray();
            if (matching.Length < count) return Decision(null, PlacementReason.WaitingForMachine);
            cohort = matching.Select(agent => agent.Id).ToHashSet();
        }
        PlacementAgent? best = null;
        PlacementAgent? next = null;
        var bestWarm = false;
        var tied = false;
        foreach (var agent in agents)
        {
            if (!Eligible(agent) || agent.SlotsUsed >= agent.MaxTasks ||
                agent.AvailableRamMb < request.RequiredRamMb || (pinned is not null && agent.Id != pinned) ||
                (cohort is not null && !cohort.Contains(agent.Id)))
                continue;
            var warm = agent.WarmRuns?.Contains(request.RunId) == true ||
                agent.WarmPackages?.Any(sha => string.Equals(sha, request.PackageSha256, StringComparison.OrdinalIgnoreCase)) == true;
            var comparison = best is null ? -1 : warm != bestWarm ? (warm ? -1 : 1) :
                ((long)agent.SlotsUsed * best.MaxTasks).CompareTo((long)best.SlotsUsed * agent.MaxTasks);
            if (comparison > 0) continue;
            if (comparison < 0)
            {
                best = agent;
                next = AfterCursor(agent.Id) ? agent : null;
                bestWarm = warm;
                tied = false;
                continue;
            }
            tied = true;
            if (agent.Id.CompareTo(best!.Id) < 0) best = agent;
            if (AfterCursor(agent.Id) && (next is null || agent.Id.CompareTo(next.Id) < 0)) next = agent;
        }
        var selected = next ?? best;
        return selected is null ? Decision(null, PlacementReason.WaitingForMachine) :
            Decision(selected, pinned is not null ? PlacementReason.Affinity :
                bestWarm ? PlacementReason.WarmHost : tied ? PlacementReason.RoundRobin : PlacementReason.LeastLoaded);

        bool AfterCursor(Guid id) => Snapshot.LastAgentId is not { } last || id.CompareTo(last) > 0;
        bool Eligible(PlacementAgent agent) => agent.Healthy && !agent.Draining &&
            (!request.RePlacement || agent.Id != request.PreviousAgentId) &&
            (agent.ReservedForRun is not { } reserved ||
                reserved == request.RunId && (agent.ReservedForNode is null || agent.ReservedForNode == request.NodeKey)) &&
            PlacementFilter.Matches(request.Policy?.Selector, agent);
        PlacementDecision Decision(PlacementAgent? agent, PlacementReason reason) => new(request, agent, reason, owner, Snapshot.Revision);
    }

    // Apply only after the Dispatched event is durable; proposals never consume a pin or round-robin turn.
    public void Apply(PlacementDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (decision.Owner != owner || decision.Revision != Snapshot.Revision || decision.Agent is not { } agent)
            throw new InvalidOperationException("Placement decision is stale, foreign or has no eligible agent.");
        Snapshot = Committed(Snapshot, decision.Request, agent.Id);
    }

    public void RestoreRun(Guid runId, IEnumerable<(PlacementRequest Request, Guid AgentId)> attempts)
    {
        if (runId == Guid.Empty) throw new ArgumentException("Run ID must not be empty.", nameof(runId));
        ArgumentNullException.ThrowIfNull(attempts);
        var restored = PlacementSnapshot.Empty;
        foreach (var (request, agentId) in attempts)
        {
            Validate(request);
            if (request.RunId != runId || agentId == Guid.Empty)
                throw new ArgumentException("Committed placements must belong to the restored run and identify an agent.", nameof(attempts));
            restored = Committed(restored, request, agentId);
        }
        var prefix = $"{runId:D}/";
        Snapshot = new(checked(Snapshot.Revision + 1),
            Snapshot.RunPins.Remove(runId).SetItems(restored.RunPins),
            Snapshot.NodePins.RemoveRange(Snapshot.NodePins.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)))
                .SetItems(restored.NodePins),
            Snapshot.LastAgentId ?? restored.LastAgentId);
    }

    private static PlacementSnapshot Committed(PlacementSnapshot snapshot, PlacementRequest request, Guid agentId)
    {
        var nodePins = snapshot.NodePins;
        var runPins = snapshot.RunPins;
        var key = NodeKey(request.RunId, request.NodeKey);
        var kind = request.Policy?.Affinity?.Kind ?? AffinityKind.None;
        if (request.RePlacement)
        {
            if (kind == AffinityKind.Run && runPins.GetValueOrDefault(request.RunId) == request.PreviousAgentId)
            {
                runPins = runPins.SetItem(request.RunId, agentId);
                var prefix = $"{request.RunId:D}/";
                foreach (var (node, pinned) in nodePins)
                    if (node.StartsWith(prefix, StringComparison.Ordinal) && pinned == request.PreviousAgentId)
                        nodePins = nodePins.SetItem(node, agentId);
            }
            if (kind == AffinityKind.SameMachineAs)
            {
                var target = NodeKey(request.RunId, request.Policy!.Affinity!.NodeKey!);
                if (nodePins.GetValueOrDefault(target) == request.PreviousAgentId) nodePins = nodePins.SetItem(target, agentId);
            }
            if (nodePins.GetValueOrDefault(key) == request.PreviousAgentId) nodePins = nodePins.SetItem(key, agentId);
        }
        if (!nodePins.ContainsKey(key)) nodePins = nodePins.Add(key, agentId);
        if (kind == AffinityKind.Run && !runPins.ContainsKey(request.RunId)) runPins = runPins.Add(request.RunId, agentId);
        return new(checked(snapshot.Revision + 1), runPins, nodePins, agentId);
    }

    public void ForgetRun(Guid runId)
    {
        if (runId == Guid.Empty) throw new ArgumentException("Run ID must not be empty.", nameof(runId));
        var prefix = $"{runId:D}/";
        var nodes = Snapshot.NodePins.RemoveRange(Snapshot.NodePins.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)));
        Snapshot = Snapshot with
        {
            Revision = checked(Snapshot.Revision + 1), RunPins = Snapshot.RunPins.Remove(runId), NodePins = nodes
        };
    }

    private static void Validate(PlacementRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RunId == Guid.Empty || !ValidKey(request.NodeKey) || !ValidKey(request.TaskKey) || request.Attempt < 1 ||
            request.PackageSha256 is not { Length: 64 } || request.PackageSha256.Any(value => !Uri.IsHexDigit(value)) ||
            request.RequiredRamMb < 0 ||
            request.RePlacement && (request.Attempt < 2 || request.PreviousAgentId is null || request.PreviousAgentId == Guid.Empty))
            throw new ArgumentException("Invalid placement request.", nameof(request));
        if (request.Policy?.Affinity is { Kind: AffinityKind.SameMachineAs, NodeKey: { } target } && target == request.NodeKey)
            throw new ArgumentException("SameMachineAs cannot reference the requesting node.", nameof(request));
        PlacementFilter.EnsureResolved(request.Policy?.Selector);
    }

    private static void Validate(PlacementAgent agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        if (agent.Id == Guid.Empty || string.IsNullOrWhiteSpace(agent.Name) || agent.Os is not ("windows" or "linux") ||
            string.IsNullOrWhiteSpace(agent.Pool) || agent.MaxTasks < 1 || agent.SlotsUsed < 0 || agent.SlotsUsed > agent.MaxTasks ||
            agent.AvailableRamMb < 0 || agent.ReservedForRun == Guid.Empty ||
            agent.ReservedForRun is null && (agent.ReservedForNode is not null || agent.ReservationEpoch != 0) ||
            agent.ReservedForRun is not null && agent.ReservationEpoch < 1 ||
            agent.ReservedForNode is { } node && !ValidKey(node))
            throw new ArgumentException("Invalid agent capacity snapshot.", nameof(agent));
    }

    private static string NodeKey(Guid runId, string node) => $"{runId:D}/{node}";
    private static bool ValidKey(string value) => !string.IsNullOrWhiteSpace(value) && !value.Contains('/');
    private static bool ValidNodePinKey(string value) => value is { Length: > 37 } && value[36] == '/' &&
        Guid.TryParse(value.AsSpan(0, 36), out var run) && run != Guid.Empty && value[..36] == run.ToString("D") && ValidKey(value[37..]);
}
