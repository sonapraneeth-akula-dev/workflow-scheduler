using System.Collections.Immutable;
using Wf.Contracts.Definition;
using Wf.Contracts.Manifest;

namespace Wf.Validation;

public sealed record SelectorMachine(Guid Id, string Name, string Os, string Pool,
    ImmutableDictionary<string, string> Labels);

public static class SelectorValidator
{
    public static bool RequiresInventory(WorkflowManifest manifest) =>
        EffectiveNodes(manifest).SelectMany(node => node.Tasks).Any(task => Constrained(task.Placement?.Selector));

    public static ImmutableArray<ValidationError> Validate(WorkflowManifest manifest,
        IReadOnlyList<SelectorMachine> machines, IReadOnlyDictionary<string, object>? variables = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(machines);
        if (machines.Any(machine => machine is null || machine.Id == Guid.Empty ||
                string.IsNullOrWhiteSpace(machine.Name) || machine.Os is not ("windows" or "linux") ||
                string.IsNullOrWhiteSpace(machine.Pool) || machine.Labels is null) ||
            machines.Select(machine => machine.Id).Distinct().Count() != machines.Count)
            throw new ArgumentException("The registered machine inventory is invalid.", nameof(machines));
        var nodes = EffectiveNodes(manifest);
        try
        {
            PlacementResolver.Validate(nodes, manifest.Variables);
            if (variables is not null) nodes = PlacementResolver.Resolve(nodes, variables);
        }
        catch (ArgumentException error)
        {
            return [Error("$.placement", "Effective placement is invalid: " + error.Message)];
        }
        var known = machines.Select(machine => machine.Id).ToHashSet();
        var errors = ImmutableArray.CreateBuilder<ValidationError>();
        for (var nodeIndex = 0; nodeIndex < nodes.Length; nodeIndex++)
        {
            var node = nodes[nodeIndex];
            for (var taskIndex = 0; taskIndex < node.Tasks.Length; taskIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var task = node.Tasks[taskIndex];
                var selector = task.Placement?.Selector;
                if (!Constrained(selector)) continue;
                var path = $"$.nodes[{nodeIndex}].tasks[{taskIndex}].placement.selector";
                var key = $"{node.Key}/{task.Key}";
                var missing = selector!.MachineIds.FirstOrDefault(id => !known.Contains(id));
                if (missing != Guid.Empty)
                {
                    errors.Add(Error(path, $"Task '{key}' references unknown machine ID '{missing:D}'."));
                    continue;
                }
                var ids = selector.MachineIds.ToHashSet();
                var names = selector.Machines.Where(value => value.Literal is not null)
                    .Select(value => value.Literal!).ToHashSet(StringComparer.Ordinal);
                var symbolicNames = selector.Machines.Any(value => value.Variable is not null);
                var matches = machines.Count(machine =>
                    (ids.Count == 0 || ids.Contains(machine.Id)) &&
                    (selector.Os?.Literal is not { } os || machine.Os == os) &&
                    (selector.Pool?.Literal is not { } pool || machine.Pool == pool) &&
                    (selector.Machines.IsEmpty || symbolicNames || names.Contains(machine.Name)) &&
                    selector.Labels.All(label => machine.Labels.TryGetValue(label.Key, out var value) &&
                        (label.Value?.Literal is not { } expected || value == expected)));
                var count = selector.Count ?? Math.Max(1, ids.Count);
                if (matches < count)
                {
                    var labels = selector.Labels.IsEmpty ? "" :
                        " Check label keys: " + string.Join(", ", selector.Labels.Select(label => label.Key)) + ".";
                    errors.Add(Error(path, $"Task '{key}' requires {count} registered matching machine(s); found {matches}." + labels));
                }
            }
        }
        return errors.ToImmutable();
    }

    private static ValidationError Error(string path, string hint) =>
        new(RuleCatalog.MachineSelector.Id, "workflow.cs", path, RuleCatalog.MachineSelector.Message, hint);

    private static bool Constrained(PlacementSelector? selector) => selector is not null &&
        (selector.Os is not null || selector.Pool is not null || !selector.Labels.IsEmpty ||
            !selector.Machines.IsEmpty || !selector.MachineIds.IsEmpty || selector.Count is not null);

    public static ImmutableArray<NodeDefinition> EffectiveNodes(WorkflowManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest.Nodes.Select(node =>
        {
            var placement = PlacementPolicy.Inherit(manifest.Placement, node.Placement);
            return new NodeDefinition(node.Key, node.Dependencies,
                node.Tasks.Select(task => new TaskDefinition(task.Key, task.Failure, task.Policy,
                    PlacementPolicy.Inherit(placement, task.Placement), task.HttpOperation, task.Action)).ToImmutableArray(),
                node.Mode, node.MaxParallel, node.Failure, node.Inputs, node.Timeout,
                node.Placement?.Selector?.Count is not null ? node.Placement : null, node.WhenUnavailable);
        }).ToImmutableArray();
    }
}
