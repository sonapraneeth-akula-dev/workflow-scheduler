using System.Runtime.CompilerServices;
using System.Text.Json;
using Wf.Contracts;
using Wf.Contracts.Schemas;
using Wf.Sdk.Testing;
using Wf.Validation.Build;
using Wf.Validation.SemVer;

[assembly: AssemblyFixture(typeof(WorkflowFixture))]

public sealed class WorkflowValidationTests(WorkflowFixture fixture)
{
    [Fact]
    public async Task WorkflowAndSchedulesAreValidAndApiBaselineMatches()
    {
        var result = await WorkflowValidation.ValidateAsync(WorkflowFixture.Directory, fixture.Cache,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors) +
            result.BuildFailure?.StandardError + result.BuildFailure?.StandardOutput);
        var baseline = ApiBaseline.Parse(await File.ReadAllBytesAsync(
            Path.Combine(WorkflowFixture.Directory, ApiBaseline.FileName), TestContext.Current.CancellationToken));
        Assert.Equal(ApiBaseline.FromManifest(fixture.Workflow.BuildResult.Manifest).WriteUtf8(), baseline.WriteUtf8());
        using var schema = JsonDocument.Parse(await File.ReadAllBytesAsync(
            Path.Combine(WorkflowFixture.Directory, "schemas", "schedules.schema.json"),
            TestContext.Current.CancellationToken));
        Assert.True(JsonElement.DeepEquals(schema.RootElement,
            JsonSerializer.SerializeToElement(ScheduleFileSchema.Export())));
    }
}

public sealed class WorkflowSuccessTests(WorkflowFixture fixture)
{
    [Fact]
    public async Task SuccessfulTasksRunInDependencyOrderAndExposeMockedOutputs()
    {
        var artifact = JsonSerializer.SerializeToElement("sample-artifact");
        var mocks = new MockSpec()
            .WithVariable("env", JsonSerializer.SerializeToElement("ppe"))
            .Succeed("prepare/work", new Dictionary<string, JsonElement> { ["artifact"] = artifact })
            .Succeed("check/work");
        var log = await fixture.Workflow.RunAsync(mocks, TestContext.Current.CancellationToken);
        log.AssertRunStatus(RunStatus.Succeeded)
            .AssertNodeOrder("prepare", "check")
            .AssertTaskStatus("check/work", ExecutionStatus.Succeeded)
            .AssertOutput("prepare", "artifact", artifact);
    }
}

public sealed class WorkflowRetryTests(WorkflowFixture fixture)
{
    [Fact]
    public async Task TransientFailureUsesTheWorkflowRetryBudget()
    {
        var log = await fixture.Workflow.RunAsync(new MockSpec()
            .FailNThenSucceed("prepare/work", 2)
            .Succeed("check/work"), TestContext.Current.CancellationToken);
        log.AssertRunStatus(RunStatus.Succeeded)
            .AssertRetries("prepare/work", 2)
            .AssertAttemptStatuses("prepare/work",
                ExecutionStatus.Failed, ExecutionStatus.Failed, ExecutionStatus.Succeeded);
    }
}

public sealed class WorkflowFailureTests(WorkflowFixture fixture)
{
    [Fact]
    public async Task PermanentFailureSkipsDependentWork()
    {
        var log = await fixture.Workflow.RunAsync(new MockSpec()
            .Fail("prepare/work", retryable: false)
            .Succeed("check/work"), TestContext.Current.CancellationToken);
        log.AssertRunStatus(RunStatus.Failed)
            .AssertNodeStatus("prepare", ExecutionStatus.Failed)
            .AssertSkipped("check")
            .AssertNoOutputs("prepare");
    }
}

public sealed class WorkflowFixture : IAsyncLifetime
{
    public static string Directory { get; } = FindDirectory();
    public ScriptBuildCache Cache { get; } = new(
        Path.Combine(Directory, "bin", "workflow-cache"), Path.Combine(Directory, "obj", "workflow-artifacts"));
    public WorkflowUnderTest Workflow { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        Workflow = await WorkflowUnderTest.BuildAsync(Path.Combine(Directory, "workflow.cs"), Cache,
            cancellationToken: TestContext.Current.CancellationToken);
        Console.WriteLine($"Workflow publish cache hit: {Workflow.BuildResult.CacheHit}; " +
            $"publish: {Workflow.BuildResult.PublishDuration.TotalSeconds:F3}s");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static string FindDirectory([CallerFilePath] string file = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, ".."));
}
