using System.Diagnostics;
using Wf.Sdk.Testing;

namespace Wf.Validation.Tests;

[Collection("Validator")]
public sealed class ValidatorIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    [RuleCoverage("WFV001", true)]
    [RuleCoverage("WFV002", true)]
    [RuleCoverage("WFV003", true)]
    [RuleCoverage("WFV004", true)]
    public async Task RealSdkWorkflowCanBeValidatedFromXunitViaTestingFacadeAndCached()
    {
        using var fixture = new ValidatorFixture();
        fixture.WriteWorkflow();
        var timer = Stopwatch.StartNew();
        var cold = await WorkflowValidation.ValidateAsync(fixture.Source, fixture.Cache,
            fixture.AdditionalInputs, ValidatorFixture.Token);
        var coldElapsed = timer.Elapsed;
        timer.Restart();
        var warm = WorkflowValidation.Validate(fixture.Source, fixture.Cache,
            fixture.AdditionalInputs, ValidatorFixture.Token);
        var warmElapsed = timer.Elapsed;
        output.WriteLine($"Validator cold={coldElapsed.TotalSeconds:F3}s warm={warmElapsed.TotalSeconds:F3}s");
        Assert.True(cold.IsValid);
        Assert.Empty(cold.Errors);
        Assert.Null(cold.BuildFailure);
        Assert.NotNull(cold.Build);
        Assert.False(cold.Build.CacheHit);
        Assert.True(warm.IsValid);
        Assert.Empty(warm.Errors);
        Assert.Null(warm.BuildFailure);
        Assert.NotNull(warm.Build);
        Assert.True(warm.Build.CacheHit);
        Assert.Equal(cold.Build.CacheKey, warm.Build.CacheKey);
        Assert.Equal(cold.Build.ManifestUtf8.ToArray(), warm.Build.ManifestUtf8.ToArray());
        Assert.Equal("validator/integration", warm.Build.Manifest.Name);
        Assert.Equal(2, warm.Build.Manifest.Nodes.Length);
        Assert.Equal("environment", Assert.Single(warm.Build.Manifest.Variables).Key);
        Assert.True(File.Exists(warm.Build.AssemblyPath));
        Assert.True(warmElapsed < TimeSpan.FromSeconds(10), $"Warm validation exceeded 10s: {warmElapsed}");

        var manifest = Path.Combine(Path.GetDirectoryName(warm.Build.PublishDirectory)!, "manifest.json");
        File.WriteAllText(manifest, "corrupt");
        await Assert.ThrowsAsync<InvalidDataException>(() => Validator.ValidateAsync(fixture.Source,
            fixture.Cache, fixture.AdditionalInputs, ValidatorFixture.Token));
    }

    [Fact]
    public async Task DefaultCacheLivesInIgnoredBinAndObjAndDoesNotInvalidateItself()
    {
        using var fixture = new ValidatorFixture();
        fixture.WriteWorkflow();
        var first = await Validator.ValidateAsync(fixture.Source,
            additionalInputs: fixture.AdditionalInputs, cancellationToken: ValidatorFixture.Token);
        var second = await Validator.ValidateAsync(fixture.Source,
            additionalInputs: fixture.AdditionalInputs, cancellationToken: ValidatorFixture.Token);
        Assert.True(first.IsValid);
        Assert.True(second.IsValid);
        Assert.NotNull(first.Build);
        Assert.NotNull(second.Build);
        Assert.StartsWith(Path.Combine(fixture.Source, "bin"), first.Build.PublishDirectory);
        Assert.False(first.Build.CacheHit);
        Assert.True(second.Build.CacheHit);
    }

    [Fact]
    [RuleCoverage("WFV003", false)]
    public async Task CompilerFailureReturnsStableErrorAndPreservesRawDiagnostics()
    {
        using var fixture = new ValidatorFixture();
        File.WriteAllText(fixture.Script, "this is not valid C#;");
        var first = await Validator.ValidateAsync(fixture.Source, fixture.Cache,
            cancellationToken: ValidatorFixture.Token);
        Assert.False(first.IsValid);
        Assert.Equal(new("WFV003", "workflow.cs", "$", "The workflow script could not be built.",
            "Fix the compiler or restore diagnostics and validate again."), Assert.Single(first.Errors));
        Assert.Null(first.Build);
        Assert.NotNull(first.BuildFailure);
        Assert.Equal("publish", first.BuildFailure.Stage);
        Assert.NotEqual(0, first.BuildFailure.ExitCode);
        Assert.Contains("error CS", first.BuildFailure.StandardOutput + first.BuildFailure.StandardError);
        Assert.Empty(Directory.GetDirectories(fixture.CacheDirectory));
    }

    [Fact]
    [RuleCoverage("WFV004", false)]
    public async Task ActualSdkDefinitionFailureDuringDescribeReturnsCatalogueError()
    {
        using var fixture = new ValidatorFixture();
        fixture.WriteWorkflow("""
            var workflow = Workflow.Define("validator/invalid", "1.0.0", w => w
                .Type("release")
                .Node("deploy", n => n.DependsOn("missing")
                    .Task("work", _ => Task.CompletedTask)));
            await ManifestWriter.WriteAsync(workflow, Console.OpenStandardOutput());
            """);
        var result = await Validator.ValidateAsync(fixture.Source, fixture.Cache,
            fixture.AdditionalInputs, ValidatorFixture.Token);
        Assert.False(result.IsValid);
        Assert.Equal(new("WFV004", "workflow.cs", "$", "The workflow script failed during --describe.",
            "Fix the workflow definition or --describe diagnostics and validate again."), Assert.Single(result.Errors));
        Assert.Null(result.Build);
        Assert.NotNull(result.BuildFailure);
        Assert.Equal("describe", result.BuildFailure.Stage);
        Assert.Contains("unknown node 'missing'", result.BuildFailure.StandardError);
        Assert.Empty(Directory.GetDirectories(fixture.CacheDirectory));
    }

    [Fact]
    public async Task NonzeroDescribeExitPreservesExitCodeStdoutAndStderr()
    {
        using var fixture = new ValidatorFixture();
        fixture.WriteWorkflow("""
            Console.WriteLine("describe output");
            Console.Error.WriteLine("describe error");
            Environment.Exit(23);
            """);
        var result = await Validator.ValidateAsync(fixture.Source, fixture.Cache,
            fixture.AdditionalInputs, ValidatorFixture.Token);
        Assert.False(result.IsValid);
        Assert.Equal(RuleCatalog.DescribeFailed.CreateError("workflow.cs", "$"), Assert.Single(result.Errors));
        Assert.NotNull(result.BuildFailure);
        Assert.Equal(23, result.BuildFailure.ExitCode);
        Assert.Contains("describe output", result.BuildFailure.StandardOutput);
        Assert.Contains("describe error", result.BuildFailure.StandardError);
    }

    [Theory]
    [InlineData("not JSON")]
    [InlineData("{}")]
    public async Task InvalidDescribeContractIsAnExplicitBuildFailureNotSuccess(string invalidManifest)
    {
        using var fixture = new ValidatorFixture();
        fixture.WriteWorkflow($"Console.WriteLine({System.Text.Json.JsonSerializer.Serialize(invalidManifest)});");
        await Assert.ThrowsAsync<InvalidDataException>(() => Validator.ValidateAsync(fixture.Source,
            fixture.Cache, fixture.AdditionalInputs, ValidatorFixture.Token));
        Assert.Empty(Directory.GetDirectories(fixture.CacheDirectory));
    }

    [Fact]
    public async Task CancellationDuringActualDescribePropagatesAndCommitsNothing()
    {
        using var fixture = new ValidatorFixture();
        var ready = Path.Combine(fixture.Root, "describe-ready");
        fixture.WriteWorkflow("""
            File.WriteAllText(Path.Combine("..", "describe-ready"), Environment.ProcessId.ToString());
            await Task.Delay(Timeout.Infinite);
            """);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ValidatorFixture.Token);
        cancellation.CancelAfter(TimeSpan.FromMinutes(3));
        var validation = Validator.ValidateAsync(fixture.Source, fixture.Cache,
            fixture.AdditionalInputs, cancellation.Token);
        try
        {
            await ValidatorFixture.WaitForFileAsync(ready, validation);
            var pid = int.Parse(File.ReadAllText(ready), System.Globalization.CultureInfo.InvariantCulture);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => validation);
            Assert.Empty(Directory.GetDirectories(fixture.CacheDirectory));
            Assert.True(HasExited(pid), $"Owned describe process {pid} survived cancellation.");
        }
        finally
        {
            cancellation.Cancel();
            try { await validation; }
            catch (OperationCanceledException) { }
        }
    }

    private static bool HasExited(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }
}
