using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Sdk;
using Wf.Sdk.Hosting;
using Wf.Sdk.Local;

namespace Wf.Sdk.Tests;

public sealed class DryRunTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SuccessPublishesMockOutputsWithoutInvokingAnyOriginalDelegate()
    {
        var calls = 0;
        var workflow = Workflow.Define("dry", "1", w => w.Type("test").Metadata("area", "checkout")
            .Variables(v => v.String("env", "dev"))
            .SystemInit(_ => { calls++; })
            .Node("node", n => n.Task("work", _ => { calls++; throw new InvalidOperationException("Must not execute"); }))
            .PostProcess(_ => { calls++; }));
        var spec = new DryRunSpec(new Dictionary<string, TaskMock>
        {
            ["node/work"] = new(Outputs: new Dictionary<string, JsonElement>
                { ["answer"] = JsonSerializer.SerializeToElement(42) }.ToImmutableDictionary())
        }.ToImmutableDictionary(), new());
        var log = await DryRunHost.ExecuteAsync(workflow, spec, Token);
        Assert.Equal(0, calls);
        Assert.Equal(0, log.ExitCode);
        Assert.Equal(42, log.Outputs["node"]["answer"].GetInt32());
        Assert.Equal("checkout", log.Snapshot.Metadata.Values["area"]);
        Assert.Equal("dev", log.Snapshot.Variables["env"].GetString());
        Assert.Equal(DateTimeOffset.UnixEpoch, log.Events[0].At);
        Assert.Equal(ExecutionStatus.Succeeded, log.Snapshot.Nodes["$post"]);
        var json = JsonSerializer.Serialize(log, DryRunJsonContext.Default.DryRunLog);
        Assert.Equal(log.Snapshot.Status, JsonSerializer.Deserialize(json, DryRunJsonContext.Default.DryRunLog)!.Snapshot.Status);
    }

    [Theory]
    [InlineData(MockOutcome.Fail, "TaskFailureException")]
    [InlineData(MockOutcome.Throw, "InvalidOperationException")]
    public async Task FailureAndThrowKeepErrorDetailsAndSkipSuccessDependencies(MockOutcome outcome, string errorType)
    {
        var workflow = Workflow.Define("fail", "1", w => w.Type("test")
            .Node("fail", n => n.Task("work", _ => { }))
            .Node("child", n => n.DependsOn("fail").Task("work", _ => { }))
            .Node("always", n => n.DependsOn("fail", DependencyCondition.Always).Task("cleanup", _ => { }))
            .PostProcess(_ => { }));
        var log = await DryRunHost.ExecuteAsync(workflow, Spec("fail/work", new(outcome, ErrorMessage: "Expected mock error")), Token);
        Assert.Equal(1, log.ExitCode);
        Assert.Equal(RunStatus.Failed, log.Snapshot.Status);
        Assert.Equal(ExecutionStatus.Skipped, log.Snapshot.Nodes["child"]);
        Assert.Equal(ExecutionStatus.Succeeded, log.Snapshot.Nodes["always"]);
        Assert.Equal(ExecutionStatus.Succeeded, log.Snapshot.Nodes["$post"]);
        var attempt = Assert.Single(log.Attempts, a => a.TaskKey == "fail/work");
        Assert.Equal(errorType, attempt.ErrorType);
        Assert.Equal("Expected mock error", attempt.ErrorMessage);
    }

    [Fact]
    public async Task TimeoutsAndBackoffAdvanceHoursOfVirtualTimeInUnderOneSecond()
    {
        var workflow = Workflow.Define("timeouts", "1", w => w.Type("test").Node("node", n =>
            n.Task("work", _ => { }, p => p.Timeout(TimeSpan.FromHours(1)).Retry(2, Backoff.Fixed(TimeSpan.FromMinutes(1))))));
        var watch = Stopwatch.StartNew();
        var log = await DryRunHost.ExecuteAsync(workflow, Spec("node/work", new(MockOutcome.Timeout)), Token)
            .WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"Virtual timeouts took {watch.Elapsed} real time.");
        Assert.Equal(3, log.Attempts.Length);
        Assert.All(log.Attempts, a => Assert.Equal(FailureCategory.Timeout, a.Failure));
        Assert.Equal(ExecutionStatus.TimedOut, log.Snapshot.Tasks["node/work"]);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddHours(3).AddMinutes(2), log.Snapshot.EndedAt);
    }

    [Theory]
    [InlineData(0, 2, RunStatus.Succeeded, 1)]
    [InlineData(2, 2, RunStatus.Succeeded, 3)]
    [InlineData(3, 2, RunStatus.Failed, 3)]
    public async Task FailThenSucceedHonorsAuthorRetryBudget(int failures, int retries, RunStatus status, int attempts)
    {
        var workflow = Workflow.Define("retry", "1", w => w.Type("test").Node("node", n =>
            n.Task("work", _ => { }, p => p.Retry(retries, Backoff.Exponential(TimeSpan.FromMinutes(1))))));
        var log = await DryRunHost.ExecuteAsync(workflow,
            Spec("node/work", new(MockOutcome.FailThenSucceed, FailuresBeforeSuccess: failures)), Token);
        Assert.Equal(status, log.Snapshot.Status);
        Assert.Equal(attempts, log.Attempts.Length);
        Assert.Equal(Enumerable.Range(1, attempts), log.Attempts.Select(a => a.Attempt));
    }

    [Theory]
    [InlineData(false, DependencyCondition.OnSuccess, ExecutionStatus.Skipped)]
    [InlineData(false, DependencyCondition.OnFailure, ExecutionStatus.Succeeded)]
    [InlineData(false, DependencyCondition.Always, ExecutionStatus.Succeeded)]
    [InlineData(true, DependencyCondition.OnSuccess, ExecutionStatus.Succeeded)]
    [InlineData(true, DependencyCondition.OnFailure, ExecutionStatus.Skipped)]
    [InlineData(true, DependencyCondition.Always, ExecutionStatus.Succeeded)]
    public async Task NodeFailurePolicyAndDependencyConditionsUseTheRealCore(bool soft, DependencyCondition condition,
        ExecutionStatus expected)
    {
        var workflow = Workflow.Define("policies", "1", w => w.Type("test")
            .Node("source", n => n.OnFailure(soft ? Failure.Soft : Failure.Hard).Task("work", _ => { }))
            .Node("child", n => n.DependsOn("source", condition).Task("work", _ => { })));
        var log = await DryRunHost.ExecuteAsync(workflow, Spec("source/work", new(MockOutcome.Fail)), Token);
        Assert.Equal(expected, log.Snapshot.Nodes["child"]);
        Assert.Equal(soft ? RunStatus.Succeeded : RunStatus.Failed, log.Snapshot.Status);
    }

    [Fact]
    public async Task PlatformRetriesAndNonRetryableFailuresRespectIndependentBudgets()
    {
        var workflow = Workflow.Define("platform", "1", w => w.Type("test").Node("node", n =>
            n.Task("work", _ => { }, p => p.Retry(2))));
        var platform = await DryRunHost.ExecuteAsync(workflow, Spec("node/work",
            new(MockOutcome.FailThenSucceed, FailuresBeforeSuccess: 2, Category: FailureCategory.AgentLost)), Token);
        Assert.Equal(0, platform.ExitCode);
        Assert.Equal(3, platform.Attempts.Length);
        var permanent = await DryRunHost.ExecuteAsync(workflow, Spec("node/work",
            new(MockOutcome.FailThenSucceed, FailuresBeforeSuccess: 1, Retryable: false)), Token);
        Assert.Equal(1, permanent.ExitCode);
        Assert.Single(permanent.Attempts);
    }

    [Fact]
    public async Task SuccessEventSequenceMatchesLocalEngineWithoutSpecialSchedulingSemantics()
    {
        var workflow = Workflow.Define("parity", "1", w => w.Type("test").SystemInit(_ => { })
            .Node("first", n => n.Task("one", _ => { }).Task("two", _ => { }))
            .Node("second", n => n.DependsOn("first").Task("work", _ => { }))
            .PostProcess(_ => { }).SystemPostProcess(_ => { }));
        var dry = await DryRunHost.ExecuteAsync(workflow, new(Default: new()), Token);
        var local = await new LocalEngine(workflow, output: TextWriter.Null).RunAsync(Token);
        Assert.Equal(local.Events.Select(e => (e.Sequence, e.Entity, e.Key, e.State)),
            dry.Events.Select(e => (e.Sequence, e.Entity, e.Key, e.State)));
    }

    [Fact]
    public async Task MissingUnknownAndInvalidMocksFailBeforeExecutionRatherThanDefaultingToSuccess()
    {
        var workflow = Workflow.Define("invalid", "1", w => w.Type("test").Node("node", n => n.Task("work", _ => { })));
        Assert.Contains("node/work", (await Assert.ThrowsAsync<ArgumentException>(() =>
            DryRunHost.ExecuteAsync(workflow, new(), Token))).Message);
        Assert.Contains("unknown/work", (await Assert.ThrowsAsync<ArgumentException>(() =>
            DryRunHost.ExecuteAsync(workflow, Spec("unknown/work", new()), Token))).Message);
        foreach (var mock in new[] { new TaskMock((MockOutcome)99), new(FailuresBeforeSuccess: -1),
            new(Category: (FailureCategory)99), new(ErrorMessage: "") })
            Assert.Contains("node/work", (await Assert.ThrowsAsync<ArgumentException>(() =>
                DryRunHost.ExecuteAsync(workflow, Spec("node/work", mock), Token))).Message);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"defualt\":{}}", DryRunJsonContext.Default.DryRunSpec));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"default\":{\"outcome\":\"Unknown\"}}", DryRunJsonContext.Default.DryRunSpec));
        var nullMock = JsonSerializer.Deserialize("{\"tasks\":{\"node/work\":null}}", DryRunJsonContext.Default.DryRunSpec)!;
        Assert.Contains("node/work", (await Assert.ThrowsAsync<ArgumentException>(() =>
            DryRunHost.ExecuteAsync(workflow, nullMock, Token))).Message);
    }

    [Fact]
    public async Task HostModePrintsOnlyJsonAndCommandLineOverridesSpecVariables()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wf-dry-run-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path, "{\"default\":{\"outcome\":\"Succeed\"},\"variables\":{\"env\":\"spec\"}}", Token);
            var workflow = Workflow.Define("host", "1", w => w.Type("test").Variables(v => v.String("env", "default"))
                .Node("node", n => n.Task("never", _ => throw new InvalidOperationException("User code executed"))));
            using var output = new StringWriter();
            Assert.Equal(0, await DryRunHost.RunAsync(workflow, ["--dry-run", path, "--var", "env=cli"], output, Token));
            var log = JsonSerializer.Deserialize(output.ToString(), DryRunJsonContext.Default.DryRunLog)!;
            Assert.Equal("cli", log.Snapshot.Variables["env"].GetString());
            Assert.Equal(RunStatus.Succeeded, log.Snapshot.Status);
            Assert.DoesNotContain("User code executed", output.ToString());
        }
        finally { File.Delete(path); }
    }

    private static DryRunSpec Spec(string key, TaskMock mock) =>
        new(new Dictionary<string, TaskMock> { [key] = mock }.ToImmutableDictionary(), new());
}
