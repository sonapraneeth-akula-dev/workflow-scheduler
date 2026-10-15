using System.Collections.Immutable;
using System.Text.Json;
using Wf.Contracts.Definition;
using Wf.Contracts.Runs;
using Wf.Validation;

namespace Wf.ControlPlane.Tests;

public sealed class TriggerRunTests
{
    [Theory]
    [InlineData(VariableType.String, "\"hello\"")]
    [InlineData(VariableType.Int, "42")]
    [InlineData(VariableType.Long, "9223372036854775807")]
    [InlineData(VariableType.Double, "1.5")]
    [InlineData(VariableType.Bool, "false")]
    [InlineData(VariableType.Enum, "\"Dev\"")]
    [InlineData(VariableType.StringList, "[\"a\",\"b\"]")]
    [InlineData(VariableType.Duration, "\"1.02:03:04\"")]
    [InlineData(VariableType.Secret, "{\"Name\":\"deploy-token\"}")]
    public void Resolves_json_values_and_defaults_without_retaining_input_document(VariableType type, string json)
    {
        var schema = new VariableSchema("input", type, true, Json(json),
            type == VariableType.Enum ? ["Dev", "Prod"] : []);
        var defaults = VariableBinder.Resolve([schema], ImmutableDictionary<string, JsonElement>.Empty,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(defaults.IsValid);
        VariableResolutionResult explicitValue;
        using (var document = JsonDocument.Parse(json))
            explicitValue = VariableBinder.Resolve([schema],
                new Dictionary<string, JsonElement> { ["input"] = document.RootElement },
                cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(explicitValue.IsValid);
        Assert.Equal(json, explicitValue.Values["input"].GetRawText());
        Assert.Equal(json, defaults.Values["input"].GetRawText());
    }

    [Fact]
    public void Collects_precise_errors_in_stable_order_without_partial_values_or_input_leaks()
    {
        var result = VariableBinder.Resolve([
            new("required", VariableType.Int, true), new("good", VariableType.Bool),
            new("typed", VariableType.Int), new("optional", VariableType.String)],
            new Dictionary<string, JsonElement>
            {
                ["typed"] = Json("\"never-echo-this-value\""), ["unknown"] = Json("1"), ["good"] = Json("true")
            }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(result.IsValid);
        Assert.Empty(result.Values);
        Assert.Equal(["$.variables[\"required\"]", "$.variables[\"typed\"]", "$.variables[\"unknown\"]"],
            result.Errors.Select(error => error.Path));
        Assert.All(result.Errors, error =>
        {
            Assert.Equal("WFV025", error.RuleId);
            Assert.Equal("request", error.File);
            Assert.False(string.IsNullOrWhiteSpace(error.Hint));
            Assert.DoesNotContain("never-echo-this-value", error.ToString());
        });
    }

    [Theory]
    [InlineData(VariableType.String, "\"\\u0000\"")]
    [InlineData(VariableType.StringList, "[\"\\u0000\"]")]
    [InlineData(VariableType.Int, "2147483648")]
    [InlineData(VariableType.Long, "9223372036854775808")]
    [InlineData(VariableType.Double, "1e999")]
    [InlineData(VariableType.Duration, "\"5m\"")]
    [InlineData(VariableType.Secret, "{\"Name\":\"token\",\"Value\":\"secret\"}")]
    [InlineData(VariableType.Bool, "null")]
    public void Rejects_nonpersistable_or_invalid_values(VariableType type, string json)
    {
        var result = VariableBinder.Resolve([new("input", type)],
            new Dictionary<string, JsonElement> { ["input"] = Json(json) },
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(result.IsValid);
        Assert.Single(result.Errors);
        Assert.Empty(result.Values);
    }

    [Fact]
    public void Explicit_values_override_defaults_and_optional_values_stay_absent()
    {
        var result = VariableBinder.Resolve([new("count", VariableType.Int, true, Json("3")),
            new("optional", VariableType.String)], new Dictionary<string, JsonElement> { ["count"] = Json("4") },
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.IsValid);
        Assert.Equal(4, Assert.Single(result.Values).Value.GetInt32());
        Assert.Throws<OperationCanceledException>(() => VariableBinder.Resolve([], result.Values,
            cancellationToken: new CancellationToken(canceled: true)));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"workflowId\":null}")]
    [InlineData("{\"workflowId\":\"a/b/c\",\"variables\":null}")]
    [InlineData("{\"workflowId\":\"a/b/c\",\"placement\":{}}")]
    [InlineData("{\"workflowId\":\"a/b/c\",\"workflowId\":\"d/e/f\"}")]
    [InlineData("{\"workflowId\":\"a/b/c\",\"variables\":{\"x\":1,\"x\":2}}")]
    public void Strict_request_rejects_missing_null_unknown_or_duplicate_properties(string json) =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json, RunJsonContext.Default.TriggerRunRequest));

    [Fact]
    public void Shared_contract_defaults_empty_variables_and_preserves_forward_compatible_sdk_response()
    {
        var request = JsonSerializer.Deserialize("{\"workflowId\":\"a/b/c\"}", RunJsonContext.Default.TriggerRunRequest)!;
        Assert.Empty(request.Variables);
        Assert.Null(request.Version);
        var id = Guid.CreateVersion7();
        var response = JsonSerializer.Deserialize($"{{\"runId\":\"{id}\",\"future\":1}}", RunJsonContext.Default.TriggerRunResponse);
        Assert.Equal(id, response!.RunId);
        Assert.Equal(Guid.Empty, JsonSerializer.Deserialize("{}", RunJsonContext.Default.TriggerRunResponse)!.RunId);
    }

    private static JsonElement Json(string json) => JsonSerializer.Deserialize<JsonElement>(json);
}
