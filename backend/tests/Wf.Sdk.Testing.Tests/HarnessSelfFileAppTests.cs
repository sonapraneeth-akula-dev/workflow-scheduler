using System.Text.Json;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Sdk.Hosting;
using Wf.Validation.Build;

namespace Wf.Sdk.Testing.Tests;

[Collection("HarnessSelfFileApp")]
public sealed class HarnessSelfFileAppTests(HarnessSelfFileAppFixture fixture)
{
    [Theory]
    [InlineData(MockOutcome.Succeed, 0, RunStatus.Succeeded, 0)]
    [InlineData(MockOutcome.Fail, 0, RunStatus.Failed, 0)]
    [InlineData(MockOutcome.Throw, 0, RunStatus.Failed, 2)]
    [InlineData(MockOutcome.Timeout, 0, RunStatus.Failed, 2)]
    [InlineData(MockOutcome.FailThenSucceed, 2, RunStatus.Succeeded, 2)]
    [InlineData(MockOutcome.FailThenSucceed, 3, RunStatus.Failed, 2)]
    public async Task HarnessSelfRealPublishedFileAppUsesPersistedEventsAcrossEveryMockOutcome(
        MockOutcome outcome, int failures, RunStatus status, int retries)
    {
        var outputs = new Dictionary<string, JsonElement> { ["answer"] = JsonSerializer.SerializeToElement(42) };
        var spec = new MockSpec(new TaskMock()).Fail("soft/work", retryable: false);
        spec = outcome switch
        {
            MockOutcome.Succeed => spec.Succeed("source/work", outputs),
            MockOutcome.Fail => spec.Fail("source/work", "expected", retryable: false),
            MockOutcome.Timeout => spec.Timeout("source/work"),
            MockOutcome.Throw => spec.Throw("source/work", "expected"),
            MockOutcome.FailThenSucceed => spec.FailNThenSucceed("source/work", failures, outputs),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome))
        };
        var log = await fixture.Workflow.RunAsync(spec, TestContext.Current.CancellationToken);
        var succeeds = status == RunStatus.Succeeded;
        var failed = outcome == MockOutcome.Timeout ? ExecutionStatus.TimedOut : ExecutionStatus.Failed;
        var attempts = Enumerable.Repeat(failed, retries + 1).ToArray();
        attempts[^1] = succeeds ? ExecutionStatus.Succeeded : failed;
        log.AssertRunStatus(status)
            .AssertTaskStatus("source/work", attempts[^1])
            .AssertRetries("source/work", retries)
            .AssertAttemptStatuses("source/work", attempts)
            .AssertNodeStatus("success", succeeds ? ExecutionStatus.Succeeded : ExecutionStatus.Skipped)
            .AssertNodeStatus("failure", succeeds ? ExecutionStatus.Skipped : ExecutionStatus.Succeeded)
            .AssertNodeStatus("always", ExecutionStatus.Succeeded)
            .AssertNodeStatus("soft", ExecutionStatus.SoftFailed)
            .AssertTaskStatus("soft/work", ExecutionStatus.SoftFailed)
            .AssertStageOrder("$init", "$pre", "$post", "$sys-post")
            .AssertNodeOrder("$pre", "source", "$post")
            .AssertNodeOrder("source", "always", "$post")
            .AssertSkipped(succeeds ? "failure" : "success");
        foreach (var stage in LifecycleStages.Before.Concat(LifecycleStages.After))
            log.AssertStageStatus(stage).AssertAttemptStatuses(stage + "/run", ExecutionStatus.Succeeded);
        if (succeeds)
            log.AssertOutput("source", "answer", JsonSerializer.SerializeToElement(42));
        else
            log.AssertNoOutputs("source");
        var runDirectories = Path.Combine(fixture.Source, "obj", "wf-sdk-testing-runs");
        Assert.True(!Directory.Exists(runDirectories) || !Directory.EnumerateDirectories(runDirectories).Any(),
            "WorkflowUnderTest must clean up only its per-run mock files.");
    }
}

public sealed class HarnessSelfFileAppFixture : IAsyncLifetime
{
    private readonly string repository = FindRepository();
    private readonly string root = Path.Combine(FindRepository(), ".wfh" + Guid.NewGuid().ToString("N")[..12]);
    public string Source => Path.Combine(root, "s");
    public WorkflowUnderTest Workflow { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(Source);
        var script = Path.Combine(Source, "a.cs");
        var sdk = Path.Combine(repository, "backend", "src", "Wf.Sdk", "Wf.Sdk.csproj");
        try
        {
            await File.WriteAllTextAsync(script, $$"""
                #:project {{sdk}}
                #:property PublishAot=false
                using Wf.Sdk;
                using Wf.Sdk.Hosting;
                using Wf.Contracts.Definition;

                var workflow = Workflow.Define("assertions", "1", w => w.Type("test")
                    .SystemInit((TaskContext _) => throw new Exception("Real init executed"))
                    .PreSetup((TaskContext _) => throw new Exception("Real pre executed"))
                    .Node("source", n => n.Task("work", _ => throw new Exception("Real source executed"),
                        p => p.Timeout(TimeSpan.FromMinutes(1)).Retry(2)))
                    .Node("success", n => n.DependsOn("source").Task("work", _ => throw new Exception("Real child executed")))
                    .Node("failure", n => n.DependsOn("source", DependencyCondition.OnFailure).Task("work", _ => { }))
                    .Node("always", n => n.DependsOn("source", DependencyCondition.Always).Task("work", _ => { }))
                    .Node("soft", n => n.Task("work", _ => { }, Failure.Soft))
                    .PostProcess((TaskContext _) => throw new Exception("Real post executed"))
                    .SystemPostProcess((TaskContext _) => throw new Exception("Real system post executed")));
                return await RunModeDispatcher.RunAsync(workflow, args);
                """, TestContext.Current.CancellationToken);
            var inputs = new[] { "Wf.Sdk", "Wf.Contracts", "Wf.Engine.Core", "Wf.Validation" }
                .Select(name => Path.Combine(repository, "backend", "src", name)).ToArray();
            Workflow = await WorkflowUnderTest.BuildAsync(script,
                new ScriptBuildCache(Path.Combine(root, "c"), Path.Combine(root, "a")),
                additionalInputs: inputs, cancellationToken: TestContext.Current.CancellationToken);
        }
        catch
        {
            Directory.Delete(root, recursive: true);
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
        return ValueTask.CompletedTask;
    }

    private static string FindRepository([System.Runtime.CompilerServices.CallerFilePath] string file = "")
    {
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(file)!); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "backend", "src", "Wf.Sdk", "Wf.Sdk.csproj")))
                return directory.FullName;
        throw new DirectoryNotFoundException("Harness self-tests require the repository source tree.");
    }
}

[CollectionDefinition("HarnessSelfFileApp", DisableParallelization = true)]
public sealed class HarnessSelfFileAppCollection : ICollectionFixture<HarnessSelfFileAppFixture>;
