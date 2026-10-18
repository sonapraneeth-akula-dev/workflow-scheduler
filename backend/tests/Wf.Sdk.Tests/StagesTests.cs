using Wf.Sdk;

namespace Wf.Sdk.Tests;

public sealed class StagesTests
{
    [Fact]
    public async Task StagesAreReservedNodesWithStableTaskKeysAndNoEagerExecution()
    {
        var calls = new List<string>();
        var workflow = Workflow.Define("stages", "1", w => w.Type("test")
            .SystemInit(_ => { calls.Add("init"); })
            .PreSetup(async _ => { await Task.Yield(); calls.Add("pre"); })
            .Node("work", n => n.Task("main", _ => { }))
            .PostProcess(s => s.Task("report", _ => { calls.Add("post"); })
                .Task("archive", _ => { calls.Add("archive"); }))
            .SystemPostProcess(_ => { calls.Add("sys-post"); }));
        Assert.Equal(["$init", "$pre", "work", "$post", "$sys-post"], workflow.Nodes.Select(n => n.Key));
        Assert.Empty(calls);
        foreach (var key in new[] { "$init/run", "$pre/run", "$post/report", "$post/archive", "$sys-post/run" })
            await workflow.Tasks[key](new());
        Assert.Equal(["init", "pre", "post", "archive", "sys-post"], calls);
    }

    [Theory]
    [InlineData("$init")]
    [InlineData("$pre")]
    [InlineData("$post")]
    [InlineData("$sys-post")]
    [InlineData("$custom")]
    public void UserCannotDeclareReservedNodeKeys(string key)
    {
        var error = Assert.Throws<ArgumentException>(() => Workflow.Define("stages", "1", w =>
            w.Type("test").Node(key, _ => { })));
        Assert.Contains(key, error.Message);
    }

    [Fact]
    public void StageBuildersSupportAllStagesAndRejectDuplicates()
    {
        var workflow = Workflow.Define("stages", "1", w => w.Type("test")
            .SystemInit(s => s.Task("one", _ => { }))
            .PreSetup(s => s.Task("two", _ => { }))
            .PostProcess((Action<TaskContext>)(_ => { }))
            .SystemPostProcess(s => s.Task("four", _ => { })));
        Assert.Equal(4, workflow.Nodes.Length);
        var error = Assert.Throws<ArgumentException>(() => Workflow.Define("stages", "1", w =>
            w.Type("test").SystemInit((Action<TaskContext>)(_ => { })).SystemInit((Action<TaskContext>)(_ => { }))));
        Assert.Contains("$init", error.Message);
    }

    [Fact]
    public void StageCallbacksAndTasksCannotBeNull()
    {
        Assert.Throws<ArgumentNullException>(() => Workflow.Define("stages", "1", w =>
            w.Type("test").SystemInit((Action<TaskContext>)null!)));
        Assert.Throws<ArgumentNullException>(() => Workflow.Define("stages", "1", w =>
            w.Type("test").PreSetup((Func<TaskContext, Task>)null!)));
        Assert.Throws<ArgumentNullException>(() => Workflow.Define("stages", "1", w =>
            w.Type("test").PostProcess((Func<StageBuilder, StageBuilder>)null!)));
    }
}
