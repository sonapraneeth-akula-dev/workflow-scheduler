using System.Collections.Immutable;
using System.Text.Json;
using Wf.Contracts.Definition;
using Wf.Validation;

namespace Wf.Validation.Tests;

public sealed class VariableBinderTests
{
    [Fact]
    [RuleCoverage("WFV025", true)]
    public void Offline_resolution_accepts_typed_overrides_and_required_defaults()
    {
        var schema = new VariableSchema("input", VariableType.Int, true, Json("3"));
        var defaults = VariableBinder.Resolve([schema], ImmutableDictionary<string, JsonElement>.Empty,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(defaults.IsValid);
        Assert.Empty(defaults.Errors);
        Assert.Equal(3, defaults.Values["input"].GetInt32());
        var overridden = VariableBinder.Resolve([schema], new Dictionary<string, JsonElement> { ["input"] = Json("4") },
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(overridden.IsValid);
        Assert.Equal(4, overridden.Values["input"].GetInt32());
    }

    [Theory]
    [InlineData("{}", "input")]
    [InlineData("""{"input":"do-not-echo"}""", "input")]
    [InlineData("""{"input":null}""", "input")]
    [InlineData("""{"input":1,"unknown":"do-not-echo"}""", "unknown")]
    [RuleCoverage("WFV025", false)]
    public void Offline_resolution_rejects_missing_invalid_or_unknown_values_with_the_shared_rule(string json, string key)
    {
        using var document = JsonDocument.Parse(json);
        var values = document.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => property.Value);
        var result = VariableBinder.Resolve([new("input", VariableType.Int, true)], values,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(result.IsValid);
        Assert.Empty(result.Values);
        Assert.Equal(RuleCatalog.TriggerVariable.CreateError("request", $"$.variables[\"{key}\"]"), Assert.Single(result.Errors));
        Assert.DoesNotContain("do-not-echo", result.Errors[0].ToString());
    }

    [Theory]
    [InlineData(VariableType.String, "\"hello\"", typeof(string))]
    [InlineData(VariableType.Int, "42", typeof(int))]
    [InlineData(VariableType.Long, "9223372036854775807", typeof(long))]
    [InlineData(VariableType.Double, "1.5", typeof(double))]
    [InlineData(VariableType.Bool, "false", typeof(bool))]
    [InlineData(VariableType.Enum, "\"Dev\"", typeof(string))]
    [InlineData(VariableType.StringList, "[\"a\",\"b\"]", typeof(ImmutableArray<string>))]
    [InlineData(VariableType.Duration, "\"1.02:03:04\"", typeof(TimeSpan))]
    [InlineData(VariableType.Secret, "{\"Name\":\"deploy-token\"}", typeof(SecretReference))]
    public void BindsTypedValues(VariableType type, string json, Type expectedType)
    {
        var schema = new VariableSchema("input", type, allowedValues: type == VariableType.Enum ? ["Dev", "Prod"] : []);
        var bound = VariableBinder.Bind([schema], new Dictionary<string, JsonElement> { ["input"] = Json(json) });
        Assert.IsType(expectedType, bound["input"]);
    }

    [Theory]
    [InlineData(VariableType.String, "null")]
    [InlineData(VariableType.String, "1")]
    [InlineData(VariableType.Int, "\"1\"")]
    [InlineData(VariableType.Int, "1.5")]
    [InlineData(VariableType.Int, "2147483648")]
    [InlineData(VariableType.Long, "9223372036854775808")]
    [InlineData(VariableType.Double, "1e999")]
    [InlineData(VariableType.Bool, "\"true\"")]
    [InlineData(VariableType.Enum, "\"Other\"")]
    [InlineData(VariableType.StringList, "[\"a\",null]")]
    [InlineData(VariableType.Duration, "\"5 minutes\"")]
    [InlineData(VariableType.Secret, "{\"Name\":\"token\",\"Value\":\"do-not-echo\"}")]
    [InlineData(VariableType.Secret, "{\"Name\":\" \"}")]
    public void RejectsWrongTypesAndInvalidValuesWithoutEchoingInputs(VariableType type, string json)
    {
        var schema = new VariableSchema("input", type, allowedValues: type == VariableType.Enum ? ["Dev"] : []);
        var error = Assert.Throws<ArgumentException>(() =>
            VariableBinder.Bind([schema], new Dictionary<string, JsonElement> { ["input"] = Json(json) }));
        Assert.Contains("input", error.Message);
        Assert.DoesNotContain("do-not-echo", error.Message);
    }

    [Fact]
    public void RequiredDefaultIsAppliedAndExplicitValueOverridesIt()
    {
        var schema = new VariableSchema("count", VariableType.Int, true, Json("3"));
        Assert.Equal(3, VariableBinder.Bind([schema], new Dictionary<string, JsonElement>())["count"]);
        Assert.Equal(4, VariableBinder.Bind([schema], new Dictionary<string, JsonElement>
            { ["count"] = Json("4") })["count"]);
    }

    [Fact]
    public void MissingRequiredAndUnknownInputsAreRejectedByName()
    {
        var required = new VariableSchema("required", VariableType.String, true);
        Assert.Contains("required", Assert.Throws<ArgumentException>(() =>
            VariableBinder.Bind([required], new Dictionary<string, JsonElement>())).Message);
        Assert.Contains("unknown", Assert.Throws<ArgumentException>(() =>
            VariableBinder.Bind([], new Dictionary<string, JsonElement> { ["unknown"] = Json("1") })).Message);
    }

    [Fact]
    public void OptionalMissingValuesStayAbsentAndDuplicateSchemasFail()
    {
        var schema = new VariableSchema("optional", VariableType.String);
        Assert.Empty(VariableBinder.Bind([schema], new Dictionary<string, JsonElement>()));
        Assert.Contains("optional", Assert.Throws<ArgumentException>(() =>
            VariableBinder.Bind([schema, schema], new Dictionary<string, JsonElement>())).Message);
    }

    [Fact]
    public void BoundValuesDoNotReferenceInputJsonDocument()
    {
        ImmutableDictionary<string, object> values;
        using (var document = JsonDocument.Parse("[\"a\"]"))
            values = VariableBinder.Bind([new("list", VariableType.StringList)],
                new Dictionary<string, JsonElement> { ["list"] = document.RootElement });
        Assert.Equal(["a"], Assert.IsType<ImmutableArray<string>>(values["list"]));
    }

    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
}
