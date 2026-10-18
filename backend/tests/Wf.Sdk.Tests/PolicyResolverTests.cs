using Wf.Contracts.Definition;
using Wf.Sdk;

namespace Wf.Sdk.Tests;

public sealed class PolicyResolverTests
{
    [Fact]
    public void Resource_caps_inherit_as_a_policy_and_explicit_zero_resets_to_unlimited()
    {
        var workflow = Workflow.Define("limits", "1", w => w.Type("test")
            .Defaults(d => d.Limits(cpuCores: 0.5, ramMb: 512))
            .Node("default", n => n.Task("work", _ => { }))
            .Node("override", n => n.Limits(cpuCores: 0.25, ramMb: 192)
                .Task("inherited", _ => { })
                .Task("unlimited", _ => { }, p => p.Limits()))
            .PostProcess(p => p.Limits(ramMb: 256).Task("cleanup", _ => { })));
        Assert.Equal(new(0.5, 512), workflow.Nodes[0].Tasks[0].Policy.Limits);
        Assert.Equal(new(0.25, 192), workflow.Nodes[1].Tasks[0].Policy.Limits);
        Assert.Equal(new(0, 0), workflow.Nodes[1].Tasks[1].Policy.Limits);
        Assert.Equal(new(0, 256), workflow.Nodes[2].Tasks[0].Policy.Limits);
    }

    [Fact]
    public void DefaultsMatchAttemptRunAndPostStageContracts()
    {
        var workflow = Workflow.Define("defaults", "1", w => w.Type("test")
            .SystemInit(_ => { }).Node("node", n => n.Task("work", _ => { })).PostProcess(_ => { })
            .SystemPostProcess(_ => { }));
        Assert.Equal(TimeSpan.FromHours(24), workflow.RunTimeout);
        Assert.All(workflow.Nodes.Take(2), node =>
        {
            Assert.Null(node.Timeout);
            Assert.Equal(TimeSpan.FromHours(1), node.Tasks[0].Policy.AttemptTimeout);
            Assert.Equal(0, node.Tasks[0].Policy.Retry.Retries);
            Assert.True(node.Tasks[0].Policy.AutoRetry);
        });
        Assert.All(workflow.Nodes.Skip(2), node =>
        {
            Assert.Equal(TimeSpan.FromMinutes(15), node.Timeout);
            Assert.Equal(TimeSpan.FromMinutes(15), node.Tasks[0].Policy.AttemptTimeout);
        });
    }

    [Fact]
    public void InnermostWinsPerPolicyFieldWithoutLosingOtherInheritedFields()
    {
        var workflow = Workflow.Define("overrides", "1", w => w.Type("test")
            .Defaults(d => d.Timeout(TimeSpan.FromMinutes(30)).Retry(1).NoAutoRetry())
            .RunTimeout(TimeSpan.FromHours(4))
            .Node("node", n => n.Timeout(TimeSpan.FromMinutes(10)).Retry(2)
                .Task("inherited", _ => { })
                .Task("override", _ => { }, t => t.Timeout(TimeSpan.FromMinutes(2)).Retry(3,
                    Backoff.Exponential(TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(1)),
                    [FailureCategory.Timeout]))));
        var node = Assert.Single(workflow.Nodes);
        Assert.Equal(TimeSpan.FromHours(4), workflow.RunTimeout);
        Assert.Equal(TimeSpan.FromMinutes(10), node.Timeout);
        Assert.Equal(2, node.Tasks[0].Policy.Retry.Retries);
        var policy = node.Tasks[1].Policy;
        Assert.Equal(TimeSpan.FromMinutes(2), policy.AttemptTimeout);
        Assert.Equal(3, policy.Retry.Retries);
        Assert.Equal(BackoffKind.Exponential, policy.Retry.Backoff.Kind);
        Assert.Equal([FailureCategory.Timeout], policy.Retry.On);
        Assert.False(policy.AutoRetry);
    }

    [Fact]
    public void NoAutoRetryDoesNotRemoveAuthorRetries()
    {
        var workflow = Workflow.Define("retry", "1", w => w.Type("test")
            .Node("node", n => n.Task("work", _ => Task.CompletedTask, t => t.Retry(3).NoAutoRetry())));
        var policy = workflow.Nodes[0].Tasks[0].Policy;
        Assert.False(policy.AutoRetry);
        Assert.Equal(3, policy.Retry.Retries);
    }

    [Fact]
    public void WorkflowDefaultsMayBeDeclaredAfterNodesWithoutChangingResolution()
    {
        var workflow = Workflow.Define("late", "1", w => w.Type("test")
            .Node("node", n => n.Task("work", _ => { }))
            .Defaults(d => d.Timeout(TimeSpan.FromSeconds(30)).Retry(2, Backoff.Fixed(TimeSpan.FromSeconds(1)))));
        Assert.Equal(TimeSpan.FromSeconds(30), workflow.Nodes[0].Tasks[0].Policy.AttemptTimeout);
        Assert.Equal(2, workflow.Nodes[0].Tasks[0].Policy.Retry.Retries);
    }

    [Fact]
    public void StageAndNodeOverridesApplyToSyncAndAsyncTasks()
    {
        var workflow = Workflow.Define("stages", "1", w => w.Type("test")
            .SystemInit(s => s.Timeout(TimeSpan.FromSeconds(10)).Retry(2).NoAutoRetry()
                .Task("sync", _ => { }, t => t.Timeout(TimeSpan.FromSeconds(2)))
                .Task("async", _ => Task.CompletedTask, t => t.Retry(1)))
            .Node("node", n => n.NoAutoRetry().Task("work", _ => { })));
        Assert.Equal(TimeSpan.FromSeconds(2), workflow.Nodes[0].Tasks[0].Policy.AttemptTimeout);
        Assert.Equal(1, workflow.Nodes[0].Tasks[1].Policy.Retry.Retries);
        Assert.False(workflow.Nodes[0].Tasks[1].Policy.AutoRetry);
        Assert.False(workflow.Nodes[1].Tasks[0].Policy.AutoRetry);
    }

    [Fact]
    public void InvalidTimeoutsRetryCountsAndKindsFailAtAuthoringBoundary()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PolicyBuilder().Timeout(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PolicyBuilder().Retry(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Backoff.Fixed(TimeSpan.FromSeconds(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BackoffPolicy((BackoffKind)99, TimeSpan.Zero, TimeSpan.Zero));
        Assert.Throws<ArgumentException>(() => new RetryPolicy(on: [FailureCategory.Timeout, FailureCategory.Timeout]));
        Assert.Throws<ArgumentException>(() => new RetryPolicy(on: [(FailureCategory)99]));
        Assert.Throws<ArgumentOutOfRangeException>(() => Workflow.Define("invalid", "1", w => w.Type("test").RunTimeout(TimeSpan.Zero)));
    }
}
