using Wf.Contracts.Definition;
using Wf.Sdk;
using Wf.Sdk.Local;
using Wf.Validation;

namespace Wf.Sdk.Tests;

public sealed class LocalVariablesTests
{
    private enum EnvironmentKind { Dev, Ppe }

    [Fact]
    public void AllVariableTypesBindThroughLocalCommandLineWithoutExecutingTasks()
    {
        var workflow = Workflow.Define("vars", "1", w => w.Type("test").Variables(v => v
            .String("text").Int("count").Long("size").Double("ratio").Bool("enabled").Enum<EnvironmentKind>("env")
            .StringList("names").Duration("duration").Secret("token")));
        var values = LocalVariables.Parse(workflow.Variables, [
            "--var", "text=with=equals", "--var", "count=3", "--var", "size=9000000000",
            "--var", "ratio=1.25", "--var", "enabled=true", "--var", "env=Dev",
            "--var", "names=[\"one\",\"two\"]", "--var", "duration=00:00:05", "--var", "token=store/token"
        ]);
        var bound = VariableBinder.Bind(workflow.Variables, values);
        Assert.Equal("with=equals", bound["text"]);
        Assert.Equal(3, bound["count"]);
        Assert.Equal(9_000_000_000L, bound["size"]);
        Assert.Equal(1.25, bound["ratio"]);
        Assert.Equal(true, bound["enabled"]);
        Assert.Equal("Dev", bound["env"]);
        Assert.Equal(["one", "two"], Assert.IsType<System.Collections.Immutable.ImmutableArray<string>>(bound["names"]));
        Assert.Equal(TimeSpan.FromSeconds(5), bound["duration"]);
        Assert.Equal(new SecretReference("store/token"), bound["token"]);
    }

    [Theory]
    [InlineData("--unknown", "count=1")]
    [InlineData("--var")]
    [InlineData("--var", "broken")]
    [InlineData("--var", "=1")]
    [InlineData("--var", "unknown=value")]
    [InlineData("--var", "count=1", "--var", "count=2")]
    public void MalformedUnknownAndDuplicateArgumentsAreRejected(params string[] args) =>
        Assert.Throws<ArgumentException>(() => LocalVariables.Parse(
            [new("count", VariableType.Int)], args));

    [Fact]
    public void InvalidTypedJsonNeverLeaksTheSuppliedValue()
    {
        var error = Assert.Throws<ArgumentException>(() => LocalVariables.Parse(
            [new("count", VariableType.Int)], ["--var", "count=do-not-echo-this"]));
        Assert.Contains("count", error.Message);
        Assert.DoesNotContain("do-not-echo-this", error.ToString());
    }
}
