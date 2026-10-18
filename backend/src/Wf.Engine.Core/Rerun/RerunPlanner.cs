using System.Collections.Immutable;
using System.Text.Json;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Engine.Core.StateMachine;

namespace Wf.Engine.Core.Rerun;

public sealed record RerunSource(Guid VersionId, RunSnapshot Snapshot, IReadOnlyDictionary<string, JsonElement> Outputs);
public sealed record ReusedNode(Guid ReusedFromRunId, JsonElement Outputs);
public sealed record RerunPlan(Guid ParentRunId, Guid VersionId, RunSnapshot Snapshot,
    ImmutableDictionary<string, ReusedNode> ReusedNodes);

public static class RerunPlanner
{
    public static RerunPlan Plan(Guid runId, RerunSource source, ImmutableArray<NodeDefinition> nodes,
        IEnumerable<string>? fromNodes = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(source.Snapshot);
        ArgumentNullException.ThrowIfNull(source.Outputs);
        var original = source.Snapshot;
        if (runId == Guid.Empty || runId == original.RunId || original.RunId == Guid.Empty || source.VersionId == Guid.Empty)
            throw new ArgumentException("Rerun and source identities must be nonempty and the run IDs must differ.");
        if (!Enum.IsDefined(original.Status) || !TransitionTable.IsTerminal(original.Status))
            throw new InvalidOperationException("Only terminal runs can be rerun.");
        if (nodes.IsDefault || original.Nodes is null || original.Tasks is null || original.Metadata is null ||
            original.Variables is null || original.LastSequence < 0)
            throw new ArgumentException("A rerun requires a complete source definition and snapshot.", nameof(source));
        var definitions = new Dictionary<string, NodeDefinition>(StringComparer.Ordinal);
        foreach (var node in nodes)
            if (node is null || !definitions.TryAdd(node.Key, node))
                throw new ArgumentException("Rerun node definitions must be nonnull and unique.", nameof(nodes));
        var expectedTasks = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes)
            foreach (var task in node.Tasks)
                if (task is null || !expectedTasks.Add($"{node.Key}/{task.Key}"))
                    throw new ArgumentException("Rerun task definitions must be nonnull and unique within a node.", nameof(nodes));
        if (original.Nodes.Count != definitions.Count || original.Nodes.Any(pair =>
                !definitions.ContainsKey(pair.Key) || !Enum.IsDefined(pair.Value)) ||
            original.Tasks.Count != expectedTasks.Count || original.Tasks.Any(pair =>
                !expectedTasks.Contains(pair.Key) || !Enum.IsDefined(pair.Value)) ||
            source.Outputs.Keys.Any(key => !definitions.ContainsKey(key)))
            throw new ArgumentException("The source snapshot and outputs must match the pinned node/task definition.", nameof(source));

        var dependents = definitions.Keys.ToDictionary(key => key, _ => new List<string>(), StringComparer.Ordinal);
        var remaining = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            remaining.Add(node.Key, node.Dependencies.Length);
            foreach (var dependency in node.Dependencies)
            {
                if (dependency is null || !dependents.TryGetValue(dependency.Node, out var children))
                    throw new ArgumentException($"Node '{node.Key}' has an unknown dependency.", nameof(nodes));
                children.Add(node.Key);
            }
        }
        var ready = new Queue<string>(remaining.Where(pair => pair.Value == 0).Select(pair => pair.Key));
        var visited = 0;
        while (ready.TryDequeue(out var key))
        {
            visited++;
            foreach (var child in dependents[key])
                if (--remaining[child] == 0) ready.Enqueue(child);
        }
        if (visited != nodes.Length) throw new ArgumentException("A rerun definition must be acyclic.", nameof(nodes));

        var invalidated = original.Nodes.Where(pair => pair.Value is not (ExecutionStatus.Succeeded or ExecutionStatus.Reused))
            .Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
        if (fromNodes is null)
        {
            if (original.Status == RunStatus.Succeeded)
                invalidated.UnionWith(definitions.Keys);
        }
        else
        {
            var selected = new HashSet<string>(StringComparer.Ordinal);
            foreach (var key in fromNodes)
                if (string.IsNullOrWhiteSpace(key) || !definitions.ContainsKey(key) || !selected.Add(key))
                    throw new ArgumentException("Rerun start nodes must be declared, nonempty and unique.", nameof(fromNodes));
            if (selected.Count == 0) throw new ArgumentException("Specify at least one rerun start node.", nameof(fromNodes));
            invalidated.UnionWith(selected);
        }
        // Setup stages implicitly precede the entire body, even without explicit DAG edges.
        if (LifecycleStages.Before.Any(invalidated.Contains))
            invalidated.UnionWith(definitions.Keys);
        var pending = new Queue<string>(invalidated);
        while (pending.TryDequeue(out var key))
            foreach (var child in dependents[key])
                if (invalidated.Add(child)) pending.Enqueue(child);

        var states = ImmutableDictionary.CreateBuilder<string, ExecutionStatus>(StringComparer.Ordinal);
        var tasks = ImmutableDictionary.CreateBuilder<string, ExecutionStatus>(StringComparer.Ordinal);
        var reused = ImmutableDictionary.CreateBuilder<string, ReusedNode>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            var reuse = !LifecycleStages.IsStage(node.Key) && !invalidated.Contains(node.Key);
            var status = reuse ? ExecutionStatus.Reused : ExecutionStatus.Pending;
            states.Add(node.Key, status);
            foreach (var task in node.Tasks) tasks.Add($"{node.Key}/{task.Key}", status);
            if (reuse)
            {
                if (!source.Outputs.TryGetValue(node.Key, out var outputs) || outputs.ValueKind != JsonValueKind.Object)
                    throw new ArgumentException($"Reusable node '{node.Key}' requires its persisted JSON object outputs (possibly empty).", nameof(source));
                reused.Add(node.Key, new(original.RunId, outputs.Clone()));
            }
        }
        var snapshot = new RunSnapshot(runId, RunStatus.Queued, 0, states.ToImmutable(), tasks.ToImmutable(), original.Metadata,
            original.Variables.ToImmutableDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal),
            AbortTargets: []);
        return new(original.RunId, source.VersionId, snapshot, reused.ToImmutable());
    }
}
