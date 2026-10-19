using System.Collections.Immutable;
using System.Text.Json;
using Wf.Contracts.Definition;
using Wf.Contracts.Manifest;

namespace Wf.Validation;

public static class ConditionRules
{
    public static void Validate(ImmutableArray<NodeDefinition> nodes, IEnumerable<VariableSchema> variables)
    {
        var types = variables.ToDictionary(variable => variable.Key, variable => variable.Type, StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            Check(node.When, node.Key, false, node.Inputs, types);
            for (var index = 0; index < node.Tasks.Length; index++)
                Check(node.Tasks[index].When, node.Key, node.Mode == NodeExecutionMode.Series && index > 0,
                    node.Inputs, types);
        }
    }

    internal static IEnumerable<ValidationError> Validate(JsonElement manifest, string file)
    {
        var types = new Dictionary<string, VariableType>(StringComparer.Ordinal);
        if (manifest.TryGetProperty("variables", out var variables))
            foreach (var variable in variables.EnumerateArray())
                if (variable.TryGetProperty("key", out var key) && key.ValueKind == JsonValueKind.String &&
                    variable.TryGetProperty("type", out var type))
                {
                    if (type.ValueKind == JsonValueKind.String &&
                        Enum.TryParse<VariableType>(type.GetString(), true, out var parsed))
                        types[key.GetString()!] = parsed;
                    else if (type.ValueKind == JsonValueKind.Number && type.TryGetInt32(out var number))
                        types[key.GetString()!] = (VariableType)number;
                }
        if (!manifest.TryGetProperty("nodes", out var nodes)) yield break;
        var nodeIndex = 0;
        foreach (var node in nodes.EnumerateArray())
        {
            var path = $"$.nodes[{nodeIndex++}]";
            var inputs = new List<InputBinding>();
            if (node.TryGetProperty("inputs", out var bindings))
                foreach (var binding in bindings.EnumerateArray())
                    inputs.Add(new(binding.GetProperty("node").GetString()!, binding.GetProperty("key").GetString()!));
            var key = node.GetProperty("key").GetString()!;
            if (Invalid(node, key, false, inputs, types))
                yield return RuleCatalog.InvalidCondition.CreateError(file, path + ".when");
            if (!node.TryGetProperty("tasks", out var tasks)) continue;
            var parallel = node.TryGetProperty("mode", out var mode) &&
                (mode.ValueKind == JsonValueKind.String
                    ? string.Equals(mode.GetString(), "Parallel", StringComparison.OrdinalIgnoreCase)
                    : mode.ValueKind == JsonValueKind.Number && mode.TryGetInt32(out var number) && number == 1);
            var taskIndex = 0;
            foreach (var task in tasks.EnumerateArray())
            {
                if (Invalid(task, key, !parallel && taskIndex > 0, inputs, types))
                    yield return RuleCatalog.InvalidCondition.CreateError(file, $"{path}.tasks[{taskIndex}].when");
                taskIndex++;
            }
        }
    }

    private static bool Invalid(JsonElement entity, string node, bool ownOutputs, IEnumerable<InputBinding> inputs,
        IReadOnlyDictionary<string, VariableType> variables)
    {
        if (!entity.TryGetProperty("when", out var when) || when.ValueKind == JsonValueKind.Null) return false;
        try
        {
            var condition = when.Deserialize(WorkflowManifestJsonContext.Default.Condition)
                ?? throw new ArgumentException("A condition cannot be null.");
            Check(condition, node, ownOutputs, inputs, variables);
            return false;
        }
        catch (Exception error) when (error is ArgumentException or JsonException)
        {
            return true;
        }
    }

    private static void Check(Condition? condition, string node, bool ownOutputs, IEnumerable<InputBinding> inputs,
        IReadOnlyDictionary<string, VariableType> variables)
    {
        if (condition is null) return;
        if (condition.VariableKey is { } variable)
        {
            if (!variables.TryGetValue(variable, out var type) || type != VariableType.Bool)
                throw new ArgumentException("Conditions require a declared Boolean variable.");
        }
        else if (condition.NodeKey == node ? !ownOutputs :
            !inputs.Contains(new InputBinding(condition.NodeKey!, condition.OutputKey!)))
            throw new ArgumentException("Conditions require a declared dependency input, or an earlier series task's output.");
    }
}
