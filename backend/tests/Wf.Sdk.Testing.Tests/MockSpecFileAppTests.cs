using System.Diagnostics;
using System.Text.Json;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Sdk.Hosting;
using Wf.Validation.Build;

namespace Wf.Sdk.Testing.Tests;

[Collection("MockSpecFileApp")]
public sealed class MockSpecFileAppTests(MockSpecFileAppFixture fixture)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Actual_published_file_app_executes_the_explicit_checkpoint_resume_branch()
    {
        var at = DateTimeOffset.Parse("2026-10-17T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var log = await fixture.Workflow.RunAsync(new MockSpec()
            .WithVariable("mode", JsonSerializer.SerializeToElement("checkpoint"))
            .WithCheckpoint("node/work", new(JsonSerializer.SerializeToElement(41), at, 7, 3)), Token);
        Assert.Equal(0, log.ExitCode);
        Assert.Equal(42, log.Outputs["node"]["position"].GetInt32());
        Assert.Equal(3, log.Outputs["node"]["attempt"].GetInt32());
        Assert.Equal(at, log.Outputs["node"]["resumedFrom"].GetDateTimeOffset());
        Assert.Equal(at, Assert.Single(log.Attempts).StartedAt);
        Assert.Empty(fixture.RunDirectories);
    }

    [Fact]
    public void BuildPublishesOnceThenUsesHashCacheAndTheActualAssemblyName()
    {
        Assert.False(fixture.Workflow.BuildResult.CacheHit);
        Assert.Equal("MockSpec.Published.dll", Path.GetFileName(fixture.Workflow.BuildResult.AssemblyPath));
        Assert.Equal("harness", fixture.Workflow.BuildResult.Manifest.Name);
        var second = WorkflowUnderTest.Build(fixture.Script, fixture.Cache,
            additionalInputs: fixture.AdditionalInputs, cancellationToken: Token);
        Assert.True(second.BuildResult.CacheHit);
        Assert.Equal(fixture.Workflow.BuildResult.CacheKey, second.BuildResult.CacheKey);
        Assert.Equal(fixture.Workflow.BuildResult.AssemblyPath, second.BuildResult.AssemblyPath);
        Assert.Equal(TimeSpan.Zero, second.BuildResult.PublishDuration);
        Assert.Equal(TimeSpan.Zero, second.BuildResult.DescribeDuration);
    }

    [Fact]
    public async Task SucceedReturnsOutputsVariablesAndEventsWithoutCallingRealTask()
    {
        var spec = new MockSpec().Succeed("node/work",
            new Dictionary<string, JsonElement> { ["answer"] = JsonSerializer.SerializeToElement(new[] { 42, 7 }) })
            .WithVariable("env", JsonSerializer.SerializeToElement("test"));
        var log = await fixture.Workflow.RunAsync(spec, Token);
        Assert.Equal(0, log.ExitCode);
        Assert.Equal(RunStatus.Succeeded, log.Snapshot.Status);
        Assert.Equal(42, log.Outputs["node"]["answer"][0].GetInt32());
        Assert.Equal("test", log.Snapshot.Variables["env"].GetString());
        Assert.NotEmpty(log.Events);
        Assert.Equal(ExecutionStatus.Succeeded, Assert.Single(log.Attempts).Status);
        Assert.Empty(fixture.RunDirectories);
    }

    [Theory]
    [InlineData(MockOutcome.Fail, "TaskFailureException")]
    [InlineData(MockOutcome.Throw, "InvalidOperationException")]
    public async Task FailAndThrowReturnFailedLogsRatherThanTreatingExitOneAsHostFailure(MockOutcome outcome, string errorType)
    {
        var spec = outcome == MockOutcome.Fail
            ? new MockSpec().Fail("node/work", "expected", retryable: false)
            : new MockSpec().Throw("node/work", "expected");
        var log = await fixture.Workflow.RunAsync(spec, Token);
        Assert.Equal(1, log.ExitCode);
        Assert.Equal(RunStatus.Failed, log.Snapshot.Status);
        Assert.Equal(ExecutionStatus.Failed, log.Snapshot.Tasks["node/work"]);
        Assert.All(log.Attempts, attempt =>
        {
            Assert.Equal(errorType, attempt.ErrorType);
            Assert.Equal("expected", attempt.ErrorMessage);
        });
        Assert.Equal(outcome == MockOutcome.Fail ? 1 : 3, log.Attempts.Length);
    }

    [Fact]
    public async Task TimeoutUsesVirtualTimeAndTheWorkflowRetryBudget()
    {
        var watch = Stopwatch.StartNew();
        var log = await fixture.Workflow.RunAsync(new MockSpec().Timeout("node/work"), Token);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"Virtual timeouts took {watch.Elapsed} real time.");
        Assert.Equal(1, log.ExitCode);
        Assert.Equal(ExecutionStatus.TimedOut, log.Snapshot.Tasks["node/work"]);
        Assert.Equal(3, log.Attempts.Length);
        Assert.All(log.Attempts, attempt => Assert.Equal(FailureCategory.Timeout, attempt.Failure));
        Assert.Equal(DateTimeOffset.UnixEpoch.AddHours(3).AddMinutes(2), log.Snapshot.EndedAt);
    }

    [Theory]
    [InlineData(0, RunStatus.Succeeded, 1)]
    [InlineData(2, RunStatus.Succeeded, 3)]
    [InlineData(3, RunStatus.Failed, 3)]
    public async Task FailNThenSucceedHonorsRetryBudgetAndOnlyPublishesOutputsOnSuccess(int failures,
        RunStatus expected, int attempts)
    {
        var log = await fixture.Workflow.RunAsync(new MockSpec().FailNThenSucceed("node/work", failures,
            new Dictionary<string, JsonElement> { ["value"] = JsonSerializer.SerializeToElement("done") }), Token);
        Assert.Equal(expected, log.Snapshot.Status);
        Assert.Equal(attempts, log.Attempts.Length);
        Assert.Equal(Enumerable.Range(1, attempts), log.Attempts.Select(attempt => attempt.Attempt));
        if (expected == RunStatus.Succeeded)
            Assert.Equal("done", log.Outputs["node"]["value"].GetString());
        else
            Assert.Empty(log.Outputs["node"]);
    }

    [Fact]
    public async Task RetryStateAndSpecFilesAreIsolatedBetweenRepeatedAndParallelRuns()
    {
        var spec = new MockSpec().FailNThenSucceed("node/work", 1);
        var logs = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => fixture.Workflow.RunAsync(spec, Token)));
        Assert.All(logs, log =>
        {
            Assert.Equal(0, log.ExitCode);
            Assert.Equal(2, log.Attempts.Length);
        });
        Assert.Equal(4, logs.Select(log => log.Snapshot.RunId).Distinct().Count());
        Assert.Empty(fixture.RunDirectories);
    }

    [Fact]
    public async Task DefaultIsExplicitAndUnknownOrMissingMocksPreserveHostDiagnostics()
    {
        var log = fixture.Workflow.Run(new MockSpec(new TaskMock()), Token);
        Assert.Equal(0, log.ExitCode);
        var missing = await Assert.ThrowsAsync<WorkflowRunException>(() => fixture.Workflow.RunAsync(new MockSpec(), Token));
        Assert.Contains("node/work", missing.StandardError);
        Assert.NotEqual(0, missing.ExitCode);
        var unknown = await Assert.ThrowsAsync<WorkflowRunException>(() =>
            fixture.Workflow.RunAsync(new MockSpec(new TaskMock()).Succeed("unknown/work"), Token));
        Assert.Contains("unknown/work", unknown.StandardError);
        Assert.Empty(fixture.RunDirectories);
    }

    [Theory]
    [InlineData("noise", 0)]
    [InlineData("empty", 0)]
    [InlineData("null", 0)]
    [InlineData("incomplete", 0)]
    [InlineData("exit", 7)]
    [InlineData("mismatch", 1)]
    public async Task InvalidOrUnexpectedProcessResponsesPreserveDiagnostics(string mode, int exitCode)
    {
        var exception = await Assert.ThrowsAsync<WorkflowRunException>(() => fixture.Workflow.RunAsync(
            new MockSpec(new TaskMock()).WithVariable("mode", JsonSerializer.SerializeToElement(mode)), Token));
        Assert.Equal(exitCode, exception.ExitCode);
        if (mode == "exit")
            Assert.Contains("host diagnostic", exception.StandardError);
        if (mode == "noise")
            Assert.Equal("not-json", exception.StandardOutput.Trim());
        Assert.Empty(fixture.RunDirectories);
    }

    [Fact]
    public async Task CancellationKillsTheEntireHostProcessTreeAndDeletesSpecFiles()
    {
        var parentFile = Path.Combine(fixture.Root, "parent.pid");
        var childFile = Path.Combine(fixture.Root, "child.pid");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var run = fixture.Workflow.RunAsync(new MockSpec(new TaskMock())
            .WithVariable("mode", JsonSerializer.SerializeToElement("wait"))
            .WithVariable("pidFile", JsonSerializer.SerializeToElement(parentFile)), cancellation.Token);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(Token);
        wait.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            while (!File.Exists(parentFile) || !File.Exists(childFile))
            {
                if (run.IsCompleted)
                    await run;
                await Task.Delay(25, wait.Token);
            }
            var parent = int.Parse(await File.ReadAllTextAsync(parentFile, Token));
            var child = int.Parse(await File.ReadAllTextAsync(childFile, Token));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            Assert.False(IsRunning(parent));
            Assert.False(IsRunning(child));
            Assert.Empty(fixture.RunDirectories);
        }
        finally
        {
            cancellation.Cancel();
            try { await run; }
            catch (OperationCanceledException) { }
            File.Delete(parentFile);
            File.Delete(childFile);
        }
    }

    [Fact]
    public async Task CancellationAndInvalidBuildInputsAreRejected()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Workflow.RunAsync(new MockSpec(new TaskMock()), cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            WorkflowUnderTest.BuildAsync(fixture.Script, fixture.Cache, cancellationToken: cancelled.Token));
        await Assert.ThrowsAsync<ArgumentException>(() => WorkflowUnderTest.BuildAsync(" ", cancellationToken: Token));
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            WorkflowUnderTest.BuildAsync(Path.Combine(fixture.Source, "missing.cs"), fixture.Cache, cancellationToken: Token));
        await Assert.ThrowsAsync<ArgumentNullException>(() => fixture.Workflow.RunAsync(null!, Token));
        Assert.Empty(fixture.RunDirectories);
    }

    [Fact]
    public async Task ChangedDeclaredInputInvalidatesThePublishedArtifact()
    {
        var root = Path.Combine(fixture.Root, "changed-source");
        var source = Path.Combine(root, "scripts");
        Directory.CreateDirectory(source);
        var script = Path.Combine(source, "changed.cs");
        File.Copy(fixture.Script, script);
        var declared = Path.Combine(root, "declared.txt");
        await File.WriteAllTextAsync(declared, "first", Token);
        var first = await WorkflowUnderTest.BuildAsync(script, sourceRoot: root,
            additionalInputs: fixture.AdditionalInputs, cancellationToken: Token);
        await File.WriteAllTextAsync(declared, "second", Token);
        var changed = await WorkflowUnderTest.BuildAsync(script, sourceRoot: root,
            additionalInputs: fixture.AdditionalInputs, cancellationToken: Token);
        Assert.False(changed.BuildResult.CacheHit);
        Assert.NotEqual(first.BuildResult.CacheKey, changed.BuildResult.CacheKey);
        Assert.StartsWith(Path.Combine(root, "bin", "wf-sdk-testing-cache"), changed.BuildResult.PublishDirectory);
        Assert.Equal(0, (await changed.RunAsync(new MockSpec(new TaskMock()), Token)).ExitCode);
    }

    private static bool IsRunning(int id)
    {
        try
        {
            using var process = Process.GetProcessById(id);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }
    }
}

public sealed class MockSpecFileAppFixture : IAsyncLifetime
{
    public string Root { get; } = Path.Combine(FindRepository(), ".wft42112", Guid.NewGuid().ToString("N"));
    public string Source => Path.Combine(Root, "source with spaces");
    public string Script => Path.Combine(Source, "mock workflow.cs");
    public ScriptBuildCache Cache => new(Path.Combine(Root, "cache"), Path.Combine(Root, "artifacts"));
    public IReadOnlyList<string> AdditionalInputs { get; } = new[] { "Wf.Sdk", "Wf.Contracts", "Wf.Engine.Core", "Wf.Validation" }
        .Select(name => Path.Combine(FindRepository(), "backend", "src", name)).ToArray();
    public WorkflowUnderTest Workflow { get; private set; } = null!;
    public IEnumerable<string> RunDirectories => Directory.Exists(Path.Combine(Source, "obj", "wf-sdk-testing-runs"))
        ? Directory.EnumerateDirectories(Path.Combine(Source, "obj", "wf-sdk-testing-runs")) : [];

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(Source);
        var sdk = Path.Combine(FindRepository(), "backend", "src", "Wf.Sdk", "Wf.Sdk.csproj");
        await File.WriteAllTextAsync(Script, $$"""
            #:project {{sdk}}
            #:property PublishAot=false
            #:property AssemblyName=MockSpec.Published
            using System.Diagnostics;
            using System.Text.Json;
            using Wf.Sdk;
            using Wf.Sdk.Hosting;

            if (args.Length > 0 && args[0] == "--child")
            {
                await File.WriteAllTextAsync(args[1], Environment.ProcessId.ToString());
                await Task.Delay(Timeout.InfiniteTimeSpan);
                return 0;
            }
            string? mode = null;
            if (args.Length > 1 && args[0] == "--dry-run")
            {
                using var spec = JsonDocument.Parse(await File.ReadAllTextAsync(args[1]));
                if (spec.RootElement.TryGetProperty("variables", out var variables) &&
                    variables.TryGetProperty("mode", out var selected))
                    mode = selected.GetString();
                switch (mode)
                {
                    case "noise": Console.WriteLine("not-json"); return 0;
                    case "empty": return 0;
                    case "null": Console.WriteLine("null"); return 0;
                    case "incomplete": Console.WriteLine("{}"); return 0;
                    case "exit": Console.Error.WriteLine("host diagnostic"); return 7;
                    case "wait":
                        var parentFile = variables.GetProperty("pidFile").GetString()!;
                        var child = new ProcessStartInfo("dotnet") { UseShellExecute = false };
                        child.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
                        child.ArgumentList.Add("--child");
                        child.ArgumentList.Add(Path.Combine(Path.GetDirectoryName(parentFile)!, "child.pid"));
                        using (Process.Start(child)) { }
                        await File.WriteAllTextAsync(parentFile, Environment.ProcessId.ToString());
                        await Task.Delay(Timeout.InfiniteTimeSpan);
                        return 0;
                }
            }
            var workflow = Workflow.Define("harness", "1", w => w.Type("test")
                .Variables(v => v.String("env", "default").String("mode", "normal"))
                .Node("node", n => n.Task("work", context =>
                {
                    if (context.Var<string>("mode") != "checkpoint" || context.Attempt.ResumedFrom is null)
                        throw new InvalidOperationException("Original task must never execute.");
                    context.Outputs["position"] = context.Checkpoint.TryLoad<int>() + 1;
                    context.Outputs["attempt"] = checked((int)context.Attempt.Number);
                    context.Outputs["resumedFrom"] = context.Attempt.ResumedFrom.Value.ToString("O");
                },
                    p => p.Timeout(TimeSpan.FromHours(1)).Retry(2, Backoff.Fixed(TimeSpan.FromMinutes(1))))));
            try
            {
                var exit = await RunModeDispatcher.RunAsync(workflow, args);
                return mode == "mismatch" ? 1 : exit;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                return 2;
            }
            """, TestContext.Current.CancellationToken);
        Workflow = await WorkflowUnderTest.BuildAsync(Script, Cache, additionalInputs: AdditionalInputs,
            cancellationToken: TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(Root))
            Directory.Delete(Root, recursive: true);
        return ValueTask.CompletedTask;
    }

    private static string FindProject([System.Runtime.CompilerServices.CallerFilePath] string file = "") => file;

    private static string FindRepository()
    {
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(FindProject())!); directory is not null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, "backend", "src", "Wf.Sdk")))
                return directory.FullName;
        throw new DirectoryNotFoundException("MockSpec file-app tests require the repository source tree.");
    }
}

[CollectionDefinition("MockSpecFileApp", DisableParallelization = true)]
public sealed class MockSpecFileAppCollection : ICollectionFixture<MockSpecFileAppFixture>;
