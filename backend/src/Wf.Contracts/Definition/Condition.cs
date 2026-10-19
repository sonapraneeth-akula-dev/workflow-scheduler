using System.Text.Json.Serialization;

namespace Wf.Contracts.Definition;

public enum ConditionOutcome { Matched, NotMatched, MissingValue, InvalidType }

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record Condition
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? VariableKey { get; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NodeKey { get; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OutputKey { get; }
    public bool Expected { get; }

    public Condition(string? variableKey = null, string? nodeKey = null, string? outputKey = null, bool expected = true)
    {
        if (variableKey is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(variableKey);
            if (nodeKey is not null || outputKey is not null)
                throw new ArgumentException("A condition must reference either a variable or a node output, not both.");
        }
        else
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(nodeKey);
            ArgumentException.ThrowIfNullOrWhiteSpace(outputKey);
        }
        VariableKey = variableKey;
        NodeKey = nodeKey;
        OutputKey = outputKey;
        Expected = expected;
    }

    public static Condition Variable(string key, bool expected = true) => new(variableKey: key, expected: expected);
    public static Condition Output(string node, string key, bool expected = true) =>
        new(nodeKey: node, outputKey: key, expected: expected);
}
