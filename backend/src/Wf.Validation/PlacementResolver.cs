using System.Collections.Immutable;
using Wf.Contracts.Definition;

namespace Wf.Validation;

public static class PlacementResolver
{
    public static void Validate(ImmutableArray<NodeDefinition> nodes, IEnumerable<VariableSchema> variables)
    {
        ArgumentNullException.ThrowIfNull(variables);
        if (nodes.All(node => node.Placement is null && node.Tasks.All(task => task.Placement is null))) return;
        var schemas = variables.ToDictionary(variable => variable.Key, StringComparer.Ordinal);
        var byKey = nodes.ToDictionary(node => node.Key, StringComparer.Ordinal);
        var graph = ImmutableArray.CreateBuilder<NodeDefinition>();
        foreach (var node in nodes)
        {
            ValidatePolicy(node.Placement, node.Key);
            var targets = new HashSet<string>(node.Dependencies.Select(dependency => dependency.Node), StringComparer.Ordinal);
            foreach (var task in node.Tasks)
            {
                ValidatePolicy(task.Placement, $"{node.Key}/{task.Key}");
                if (task.Placement?.Affinity is { Kind: AffinityKind.SameMachineAs, NodeKey: { } target })
                    targets.Add(target);
            }
            graph.Add(new(node.Key, targets.Select(target => new DependencyDefinition(target)).ToImmutableArray(), []));
        }
        DagRules.Validate(graph.ToImmutable());

        void ValidatePolicy(PlacementPolicy? policy, string key)
        {
            if (policy?.Affinity is { Kind: AffinityKind.SameMachineAs, NodeKey: { } target } &&
                (!byKey.ContainsKey(target) || target == key.Split('/')[0]))
                throw new ArgumentException($"'{key}' has an unknown or self-referencing SameMachineAs node '{target}'.");
            if (policy?.Selector is not { } selector) return;
            ValidateValue(selector.Os, false);
            ValidateValue(selector.Pool, false);
            foreach (var label in selector.Labels) ValidateValue(label.Value, false);
            foreach (var machine in selector.Machines) ValidateValue(machine, true);

            void ValidateValue(SelectorValue? value, bool allowList)
            {
                if (value?.Variable is not { } variable) return;
                if (!schemas.TryGetValue(variable, out var schema))
                    throw new ArgumentException($"'{key}' selector references unknown variable '{variable}'.");
                if (schema.Type is not (VariableType.String or VariableType.Enum) &&
                    !(allowList && schema.Type == VariableType.StringList))
                    throw new ArgumentException($"'{key}' selector variable '{variable}' must be a string/enum" +
                        (allowList ? " or string list." : "."));
            }
        }
    }

    public static ImmutableArray<NodeDefinition> Resolve(ImmutableArray<NodeDefinition> nodes, IReadOnlyDictionary<string, object> variables)
    {
        ArgumentNullException.ThrowIfNull(variables);
        if (nodes.All(node => node.Placement is null && node.Tasks.All(task => task.Placement is null))) return nodes;
        // Authoring scopes are already inherited into tasks; resolve only effective policies.
        return nodes.Select(node => new NodeDefinition(node.Key, node.Dependencies,
            node.Tasks.Select(task => new TaskDefinition(task.Key, task.Failure, task.Policy,
                ResolvePolicy(task.Placement, $"{node.Key}/{task.Key}"))).ToImmutableArray(),
            node.Mode, node.MaxParallel, node.Failure, node.Inputs, node.Timeout)).ToImmutableArray();

        PlacementPolicy? ResolvePolicy(PlacementPolicy? policy, string key)
        {
            if (policy?.Selector is not { } selector) return policy;
            return new(new PlacementSelector(Scalar(selector.Os), Scalar(selector.Pool),
                selector.Labels.Select(label => new LabelSelector(label.Key, Scalar(label.Value))).ToImmutableArray(),
                selector.Machines.SelectMany(machine => Values(machine, true)).Select(value => new SelectorValue(value))
                    .ToImmutableArray(), selector.MachineIds, selector.Count), policy.Affinity);

            SelectorValue? Scalar(SelectorValue? value) => value is null ? null : new(Values(value, false).Single());

            IEnumerable<string> Values(SelectorValue value, bool allowList)
            {
                if (value.Literal is { } literal) return [literal];
                var variable = value.Variable!;
                if (!variables.TryGetValue(variable, out var bound))
                    throw new ArgumentException($"'{key}' selector variable '{variable}' is missing.");
                if (bound is string text && !string.IsNullOrWhiteSpace(text)) return [text];
                if (allowList && bound is ImmutableArray<string> list && !list.IsDefaultOrEmpty && list.All(item => !string.IsNullOrWhiteSpace(item)))
                    return list;
                throw new ArgumentException($"'{key}' selector variable '{variable}' must contain non-empty " +
                    (allowList ? "machine names." : "text."));
            }
        }
    }
}
