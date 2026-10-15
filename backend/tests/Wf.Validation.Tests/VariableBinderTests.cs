using System.Collections.Immutable;
using System.Text.Json;
using Wf.Contracts.Definition;
using Wf.Validation;

namespace Wf.Validation.Tests;

public sealed class VariableBinderTests
{
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
