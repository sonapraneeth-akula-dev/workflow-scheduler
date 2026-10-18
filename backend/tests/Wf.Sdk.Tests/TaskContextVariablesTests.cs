using Wf.Sdk;

namespace Wf.Sdk.Tests;

public sealed class TaskContextVariablesTests
{
    private enum Environment { Dev, Prod }

    [Fact]
    public void VariablesAreTypedAndSnapshotTheInputDictionary()
    {
        var values = new Dictionary<string, object> { ["env"] = "Dev", ["count"] = 3, ["ready"] = false };
        var context = new TaskContext(variables: values);
        values["count"] = 4;
        Assert.Equal("Dev", context.Var("env"));
        Assert.Equal(Environment.Dev, context.VarEnum<Environment>("env"));
        Assert.Equal(3, context.Var<int>("count"));
        Assert.False(context.Var<bool>("ready"));
        Assert.Contains("count", Assert.Throws<InvalidOperationException>(() => context.Var<string>("count")).Message);
        Assert.Contains("missing", Assert.Throws<KeyNotFoundException>(() => context.Var("missing")).Message);
        Assert.Throws<InvalidOperationException>(() => context.VarEnum<Environment>("count"));
        Assert.Throws<InvalidOperationException>(() =>
            new TaskContext(variables: new Dictionary<string, object> { ["env"] = "Unknown" }).VarEnum<Environment>("env"));
        Assert.Equal(Environment.Prod,
            new TaskContext(variables: new Dictionary<string, object> { ["env"] = Environment.Prod }).VarEnum<Environment>("env"));
    }
}
