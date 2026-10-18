using System.Diagnostics;
using System.Text.Json;
using Wf.Contracts.Manifest;
using Wf.Validation.Build;

namespace Wf.Validation.Tests.Build;

[Collection("ScriptBuildCache")]
public sealed class ScriptBuildCacheIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task RealPublishAndDescribeAreCachedAndScheduleChangesInvalidate()
    {
        using var fixture = new ScriptBuildCacheFixture();
        var request = fixture.WriteWorkflow("""
            File.AppendAllText(Path.Combine("..", "describe-count"), "describe\n");
            """ + Environment.NewLine + ScriptBuildCacheFixture.DescribeBody);
        var timer = Stopwatch.StartNew();
        var cold = await fixture.Cache.BuildAsync(request, ScriptBuildCacheFixture.Token);
        var coldElapsed = timer.Elapsed;
        timer.Restart();
        var warm = await fixture.Cache.BuildAsync(request, ScriptBuildCacheFixture.Token);
        var warmElapsed = timer.Elapsed;
        output.WriteLine($"Cold total={coldElapsed.TotalSeconds:F3}s key={cold.KeyDuration.TotalSeconds:F3}s " +
            $"publish={cold.PublishDuration.TotalSeconds:F3}s describe={cold.DescribeDuration.TotalSeconds:F3}s");
        output.WriteLine($"Warm total={warmElapsed.TotalSeconds:F3}s key={warm.KeyDuration.TotalSeconds:F3}s " +
            $"publish={warm.PublishDuration.TotalSeconds:F3}s describe={warm.DescribeDuration.TotalSeconds:F3}s");
        Assert.False(cold.CacheHit);
        Assert.True(warm.CacheHit);
        Assert.Equal(cold.ManifestUtf8.ToArray(), warm.ManifestUtf8.ToArray());
        Assert.Equal("spike/representative", warm.Manifest.Name);
        Assert.Equal(20, warm.Manifest.Nodes.Length);
        Assert.Equal(TimeSpan.Zero, warm.PublishDuration);
        Assert.Equal(TimeSpan.Zero, warm.DescribeDuration);
        Assert.Single(File.ReadAllLines(Path.Combine(fixture.Root, "describe-count")));
        Assert.True(warmElapsed < TimeSpan.FromSeconds(10), $"Warm validate/describe exceeded 10s: {warmElapsed}");
        Assert.True(File.Exists(warm.AssemblyPath));
        File.WriteAllText(Path.Combine(fixture.Source, "schedule.json"), """{"cron":"0 10 * * *","timeZone":"America/New_York"}""");
        var changed = await fixture.Cache.BuildAsync(request, ScriptBuildCacheFixture.Token);
        Assert.False(changed.CacheHit);
        Assert.NotEqual(cold.CacheKey, changed.CacheKey);
        Assert.Equal("0 10 * * *", changed.Manifest.Metadata.Values["cron"]);
        Assert.True(File.Exists(cold.AssemblyPath));
        Assert.Equal(2, File.ReadAllLines(Path.Combine(fixture.Root, "describe-count")).Length);
    }

    [Fact]
    public async Task RawDotnetPublishAndDescribeColdAndIncrementalWarmTimings()
    {
        using var fixture = new ScriptBuildCacheFixture();
        fixture.WriteWorkflow();
        var publish = Path.Combine(fixture.Root, "raw-publish");
        byte[]? first = null;
        foreach (var scenario in new[] { "Raw cold", "Raw incremental warm" })
        {
            var timer = Stopwatch.StartNew();
            await fixture.RunDotnetAsync("publish", fixture.Script, "--configuration", "Release",
                "--output", publish, "--artifacts-path", fixture.ArtifactsDirectory,
                "-p:PublishAot=false", "-p:SelfContained=false", "-p:UseAppHost=false",
                "-p:PublishSingleFile=false", "-p:PublishTrimmed=false", "-p:UseSharedCompilation=false", "--nologo");
            var publishTime = timer.Elapsed;
            var config = Assert.Single(Directory.GetFiles(publish, "*.runtimeconfig.json"));
            var assembly = config[..^".runtimeconfig.json".Length] + ".dll";
            timer.Restart();
            var described = await fixture.RunDotnetAsync(assembly, "--describe");
            var describeTime = timer.Elapsed;
            output.WriteLine($"{scenario}: publish={publishTime.TotalSeconds:F3}s describe={describeTime.TotalSeconds:F3}s " +
                $"total={(publishTime + describeTime).TotalSeconds:F3}s");
            Assert.Equal(20, JsonSerializer.Deserialize(described, WorkflowManifestJsonContext.Default.WorkflowManifest)!.Nodes.Length);
            if (first is null)
                first = described;
            else
                Assert.Equal(first, described);
        }
    }

    [Fact]
    public async Task ConcurrentInstancesPublishOnceAndKeepExistingOutputs()
    {
        using var fixture = new ScriptBuildCacheFixture();
        var request = fixture.WriteWorkflow();
        var builds = await Task.WhenAll(
            fixture.Cache.BuildAsync(request, ScriptBuildCacheFixture.Token),
            fixture.Cache.BuildAsync(request, ScriptBuildCacheFixture.Token),
            fixture.Cache.BuildAsync(request, ScriptBuildCacheFixture.Token));
        Assert.Single(builds, result => !result.CacheHit);
        Assert.Equal(2, builds.Count(result => result.CacheHit));
        Assert.Single(builds.Select(result => result.CacheKey).Distinct());
        Assert.All(builds, result => Assert.True(File.Exists(result.AssemblyPath)));
        Assert.Empty(Directory.GetDirectories(fixture.CacheDirectory, ".staging-*"));
    }

    [Fact]
    public async Task CompilerDiagnosticsArePreservedAndNoEntryIsCommitted()
    {
        using var fixture = new ScriptBuildCacheFixture();
        File.WriteAllText(fixture.Script, "this is not valid C#;");
        var error = await Assert.ThrowsAsync<ScriptBuildException>(() =>
            fixture.Cache.BuildAsync(fixture.Request, ScriptBuildCacheFixture.Token));
        Assert.Equal("publish", error.Stage);
        Assert.NotEqual(0, error.ExitCode);
        Assert.Contains("error CS", error.StandardOutput + error.StandardError);
        Assert.Empty(Directory.GetDirectories(fixture.CacheDirectory));
    }

    [Theory]
    [InlineData("Console.WriteLine(\"not JSON\");")]
    [InlineData("Console.WriteLine(\"{}\");")]
    public async Task InvalidDescribeJsonIsNotCached(string body)
    {
        using var fixture = new ScriptBuildCacheFixture();
        var request = fixture.WriteWorkflow(body);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Cache.BuildAsync(request, ScriptBuildCacheFixture.Token));
        Assert.Empty(Directory.GetDirectories(fixture.CacheDirectory));
    }

    [Fact]
    public async Task DescribeProcessFailurePreservesStderr()
    {
        using var fixture = new ScriptBuildCacheFixture();
        var request = fixture.WriteWorkflow("""throw new InvalidOperationException("describe rejected");""");
        var error = await Assert.ThrowsAsync<ScriptBuildException>(() =>
            fixture.Cache.BuildAsync(request, ScriptBuildCacheFixture.Token));
        Assert.Equal("describe", error.Stage);
        Assert.Contains("describe rejected", error.StandardError);
        Assert.Empty(Directory.GetDirectories(fixture.CacheDirectory));
    }

    [Fact]
    public async Task CorruptManifestAssemblyAndInventoryFailWithoutRebuilding()
    {
        using var fixture = new ScriptBuildCacheFixture();
        var request = fixture.WriteWorkflow();
        var result = await fixture.Cache.BuildAsync(request, ScriptBuildCacheFixture.Token);
        var entry = Path.GetDirectoryName(result.PublishDirectory)!;
        foreach (var file in new[] { Path.Combine(entry, "manifest.json"), result.AssemblyPath, Path.Combine(entry, "integrity.json") })
        {
            var original = File.ReadAllBytes(file);
            File.WriteAllText(file, "corrupt");
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                fixture.Cache.BuildAsync(request, ScriptBuildCacheFixture.Token));
            File.WriteAllBytes(file, original);
        }
        Assert.True((await fixture.Cache.BuildAsync(request, ScriptBuildCacheFixture.Token)).CacheHit);
    }

    [Fact]
    public async Task WaitingForAnotherBuilderHonorsCancellation()
    {
        using var fixture = new ScriptBuildCacheFixture();
        var key = await fixture.Cache.ComputeKeyAsync(fixture.Request, ScriptBuildCacheFixture.Token);
        Directory.CreateDirectory(fixture.CacheDirectory);
        await using var heldLock = new FileStream(Path.Combine(fixture.CacheDirectory, key + ".lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ScriptBuildCacheFixture.Token);
        cancellation.CancelAfter(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Cache.BuildAsync(fixture.Request, cancellation.Token));
        Assert.Empty(Directory.GetDirectories(fixture.CacheDirectory));
    }

    [Fact]
    public async Task CancellationDuringRealPublishCommitsNothing()
    {
        using var fixture = new ScriptBuildCacheFixture();
        var request = fixture.WriteWorkflow();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ScriptBuildCacheFixture.Token);
        cancellation.CancelAfter(TimeSpan.FromMinutes(2));
        var build = fixture.Cache.BuildAsync(request, cancellation.Token);
        try
        {
            while (!Directory.Exists(fixture.ArtifactsDirectory))
            {
                Assert.False(build.IsCompleted, "Publish finished before cancellation could be exercised.");
                await Task.Delay(20, cancellation.Token);
            }
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => build);
            Assert.Empty(Directory.GetDirectories(fixture.CacheDirectory));
        }
        finally
        {
            cancellation.Cancel();
            try { await build; }
            catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task InputsChangingDuringDescribeAreNotCommitted()
    {
        using var fixture = new ScriptBuildCacheFixture();
        var request = fixture.WriteWorkflow("""
            File.WriteAllText(Path.Combine("..", "ready"), "ready");
            while (!File.Exists(Path.Combine("..", "continue")))
                await Task.Delay(10);
            """ + Environment.NewLine + ScriptBuildCacheFixture.DescribeBody);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ScriptBuildCacheFixture.Token);
        var build = fixture.Cache.BuildAsync(request, cancellation.Token);
        try
        {
            await ScriptBuildCacheFixture.WaitForFileAsync(Path.Combine(fixture.Root, "ready"), build);
            File.AppendAllText(Path.Combine(fixture.Source, "schedule.json"), " ");
            File.WriteAllText(Path.Combine(fixture.Root, "continue"), "continue");
            var error = await Assert.ThrowsAsync<IOException>(() => build);
            Assert.Contains("inputs changed", error.Message);
            Assert.Empty(Directory.GetDirectories(fixture.CacheDirectory));
        }
        finally
        {
            cancellation.Cancel();
            try { await build; }
            catch (IOException) { }
            catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task DescribeCancellationKillsAndReapsOnlyOwnedProcessTree()
    {
        using var fixture = new ScriptBuildCacheFixture();
        var request = fixture.WriteWorkflow("""
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false };
            start.ArgumentList.Add(typeof(Program).Assembly.Location);
            start.ArgumentList.Add("--child");
            using var child = Process.Start(start)!;
            File.WriteAllText("pids.writing", $"{Environment.ProcessId}:{child.Id}");
            File.Move("pids.writing", "pids");
            await Task.Delay(Timeout.Infinite);
            """);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ScriptBuildCacheFixture.Token);
        var build = fixture.Cache.BuildAsync(request, cancellation.Token);
        try
        {
            var pidsFile = Path.Combine(fixture.Source, "pids");
            await ScriptBuildCacheFixture.WaitForFileAsync(pidsFile, build);
            var pids = File.ReadAllText(pidsFile).Split(':').Select(int.Parse).ToArray();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => build);
            Assert.All(pids, pid => Assert.False(ScriptBuildCacheFixture.IsRunning(pid), $"Owned child {pid} survived."));
            Assert.True(ScriptBuildCacheFixture.IsRunning(Environment.ProcessId));
            Assert.Empty(Directory.GetDirectories(fixture.CacheDirectory));
        }
        finally
        {
            cancellation.Cancel();
            try { await build; }
            catch (OperationCanceledException) { }
        }
    }
}
