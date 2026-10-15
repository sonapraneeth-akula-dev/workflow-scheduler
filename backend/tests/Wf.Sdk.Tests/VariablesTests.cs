using System.Text.Json;
using Wf.Contracts.Definition;
using Wf.Sdk;

namespace Wf.Sdk.Tests;

public sealed class VariablesTests
{
    private enum Environment { Dev, Prod }

    [Fact]
    public void SupportsEveryTypeWithDefaultsAndRequiredFlags()
    {
        var workflow = Workflow.Define("vars", "1", w => w.Variables(v => v
            .String("env", "dev", required: true, allowed: ["dev", "prod"])
            .Int("count", 3).Long("size", long.MaxValue).Double("ratio", 0.5).Bool("enabled", false)
            .Enum("environment", Environment.Dev).StringList("targets", ["a", "b"])
            .Duration("delay", TimeSpan.FromSeconds(5)).Secret("token", "deploy-token")));
        Assert.Equal(System.Enum.GetValues<VariableType>(), workflow.Variables.Select(v => v.Type));
        Assert.True(workflow.Variables[0].Required);
        Assert.Equal("dev", workflow.Variables[0].Default!.Value.GetString());
        Assert.Equal(3, workflow.Variables[1].Default!.Value.GetInt32());
        Assert.Equal(long.MaxValue, workflow.Variables[2].Default!.Value.GetInt64());
        Assert.Equal(0.5, workflow.Variables[3].Default!.Value.GetDouble());
        Assert.False(workflow.Variables[4].Default!.Value.GetBoolean());
        Assert.Equal("Dev", workflow.Variables[5].Default!.Value.GetString());
        Assert.Equal(["Dev", "Prod"], workflow.Variables[5].AllowedValues);
        Assert.Equal(["a", "b"], workflow.Variables[6].Default!.Value.EnumerateArray().Select(v => v.GetString()));
        Assert.Equal("00:00:05", workflow.Variables[7].Default!.Value.GetString());
        Assert.Equal("deploy-token", workflow.Variables[8].Default!.Value.GetProperty("Name").GetString());
        Assert.Single(workflow.Variables[8].Default!.Value.EnumerateObject());
    }

    [Fact]
    public void MissingDefaultsStayAbsentForEveryType()
    {
        var workflow = Workflow.Define("vars", "1", w => w.Variables(v => v.String("s").Int("i")
            .Long("l").Double("d").Bool("b").Enum<Environment>("e").StringList("a").Duration("t").Secret("r")));
        Assert.All(workflow.Variables, v => Assert.Null(v.Default));
    }

    [Fact]
    public void DefaultsAndAllowedValuesAreCopied()
    {
        string[] allowed = ["dev", "prod"];
        string[] defaults = ["a"];
        VariablesBuilder? retained = null;
        var workflow = Workflow.Define("vars", "1", w => w.Variables(v =>
        {
            retained = v;
            v.String("env", "dev", allowed: allowed).StringList("targets", defaults);
        }));
        allowed[0] = "changed";
        defaults[0] = "changed";
        retained!.Int("later", 1);
        Assert.Equal(2, workflow.Variables.Length);
        Assert.Equal("dev", workflow.Variables[0].AllowedValues[0]);
        Assert.Equal("a", workflow.Variables[1].Default!.Value[0].GetString());
    }

    [Fact]
    public void RejectsDuplicateKeysAcrossBuilderCalls()
    {
        Assert.Throws<ArgumentException>(() => Workflow.Define("vars", "1", w =>
            w.Variables(v => v.Int("count").Bool("count"))));
        Assert.Throws<ArgumentException>(() => Workflow.Define("vars", "1", w =>
            w.Variables(v => v.Int("count")).Variables(v => v.Bool("count"))));
    }

    [Fact]
    public void RejectsInvalidDefaultsAllowedValuesAndEnumValues()
    {
        Assert.Throws<ArgumentException>(() => new VariablesBuilder().String("env", "bad", allowed: ["dev"]));
        Assert.Throws<ArgumentException>(() => new VariablesBuilder().String("env", allowed: ["dev", "dev"]));
        Assert.Throws<ArgumentException>(() => new VariablesBuilder().String("env", allowed: [null!]));
        Assert.Throws<ArgumentException>(() => new VariablesBuilder().StringList("targets", [null!]));
        Assert.Throws<ArgumentException>(() => new VariablesBuilder().Enum("env", (Environment)99));
        Assert.Throws<ArgumentException>(() => new VariablesBuilder().Enum<Environment>("env", allowed: []));
        Assert.Throws<ArgumentException>(() => new VariablesBuilder().Enum("env", Environment.Prod, allowed: [Environment.Dev]));
        Assert.Throws<ArgumentException>(() => new VariablesBuilder().Secret("token", " "));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void RejectsNonFiniteNumbers(double value) =>
        Assert.Contains("ratio", Assert.Throws<ArgumentException>(() =>
            new VariablesBuilder().Double("ratio", value)).Message);

    [Theory]
    [InlineData(VariableType.Int, "\"1\"")]
    [InlineData(VariableType.Int, "2147483648")]
    [InlineData(VariableType.Long, "9223372036854775808")]
    [InlineData(VariableType.Bool, "\"true\"")]
    [InlineData(VariableType.Duration, "\"tomorrow\"")]
    [InlineData(VariableType.Secret, "{\"Name\":\"ref\",\"Value\":\"forbidden\"}")]
    [InlineData(VariableType.Secret, "\"literal\"")]
    [InlineData(VariableType.StringList, "[1]")]
    public void ContractRejectsWrongDefaultTypeWithoutEchoingItsValue(VariableType type, string json)
    {
        var error = Assert.Throws<ArgumentException>(() =>
            new VariableSchema("key", type, @default: JsonSerializer.Deserialize<JsonElement>(json)));
        Assert.Contains("key", error.Message);
        Assert.DoesNotContain("forbidden", error.Message);
    }

    [Fact]
    public void JsonDefaultsOutliveTheirSourceDocument()
    {
        VariableSchema schema;
        using (var document = JsonDocument.Parse("\"value\""))
            schema = new("key", VariableType.String, @default: document.RootElement);
        Assert.Equal("value", schema.Default!.Value.GetString());
    }
}
