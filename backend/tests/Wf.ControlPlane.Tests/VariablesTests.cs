using System.Collections.Immutable;
using System.Text.Json;
using Wf.Contracts.Definition;
using Wf.Contracts.Manifest;
using Wf.ControlPlane.Api;

namespace Wf.ControlPlane.Tests;

public sealed class VariablesTests
{
    [Theory]
    [InlineData(VariableType.String, "\"literal\"")]
    [InlineData(VariableType.Int, "-2147483648")]
    [InlineData(VariableType.Long, "9223372036854775807")]
    [InlineData(VariableType.Double, "1.5")]
    [InlineData(VariableType.Bool, "false")]
    [InlineData(VariableType.Enum, "\"Prod\"")]
    [InlineData(VariableType.StringList, "[\"a\",\"b\"]")]
    [InlineData(VariableType.Duration, "\"-10675199.02:48:05.4775808\"")]
    [InlineData(VariableType.Secret, "{\"Name\":\"deploy-token\"}")]
    public void Projects_validated_literal_defaults_with_exact_fidelity(VariableType type, string json)
    {
        WorkflowVariable variable;
        using (var document = JsonDocument.Parse(json))
        {
            var schema = new VariableSchema("input", type, true, document.RootElement,
                type == VariableType.Enum ? ["Dev", "Prod"] : []);
            variable = Assert.Single(WorkflowVariablesResponse.From(Guid.NewGuid(), Manifest() with { Variables = [schema] }).Variables);
        }
        Assert.Equal(type, variable.Type);
        Assert.True(variable.Required);
        Assert.Equal(json, variable.Default!.Value.GetRawText());
        var encoded = JsonSerializer.Serialize(WorkflowVariablesResponse.From(Guid.NewGuid(),
            Manifest() with { Variables = [new("input", type, true, variable.Default, variable.AllowedValues.ToImmutableArray())] }),
            VariablesJsonContext.Default.WorkflowVariablesResponse);
        Assert.Contains("\"type\":\"" + type + "\"", encoded, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_uninitialized_null_or_duplicate_schemas_instead_of_empty_success()
    {
        foreach (var manifest in new[] { Manifest() with { Variables = default }, Manifest() with { Variables = [null!] },
            Manifest() with { Variables = [new("same", VariableType.String), new("same", VariableType.Int)] } })
            Assert.Throws<InvalidDataException>(() => WorkflowVariablesResponse.From(Guid.NewGuid(), manifest));
        var optional = Assert.Single(WorkflowVariablesResponse.From(Guid.NewGuid(),
            Manifest() with { Variables = [new("optional", VariableType.String)] }).Variables);
        Assert.Null(optional.Default);
        Assert.Empty(optional.AllowedValues);
    }

    private static WorkflowManifest Manifest() => new("1.0.0", "team/type/workflow", "1.0.0",
        new("type", ImmutableSortedDictionary<string, string>.Empty), [], [], [], TimeSpan.FromHours(1));
}
