using System.Text.Json;
using Wf.Contracts.Definition;

namespace Wf.Engine.Core.Scheduling;

public static class ConditionEvaluator
{
    public static ConditionOutcome Evaluate(Condition condition, IReadOnlyDictionary<string, JsonElement> variables,
        Func<string, string, JsonElement?> output)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(variables);
        ArgumentNullException.ThrowIfNull(output);
        var value = condition.VariableKey is { } key
            ? variables.TryGetValue(key, out var variable) ? variable : (JsonElement?)null
            : output(condition.NodeKey!, condition.OutputKey!);
        return value?.ValueKind switch
        {
            null => ConditionOutcome.MissingValue,
            JsonValueKind.True or JsonValueKind.False => value.Value.GetBoolean() == condition.Expected
                ? ConditionOutcome.Matched : ConditionOutcome.NotMatched,
            _ => ConditionOutcome.InvalidType
        };
    }
}
