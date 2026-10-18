using Wf.Validation.Build;

namespace Wf.Validation.Tests.Build;

[Collection("ScriptBuildCache")]
public sealed class ScriptBuildCacheKeyTests
{
    [Fact]
    public async Task SameBytesAreStableAndTimestampsDoNotMatter()
    {
        using var fixture = new ScriptBuildCacheFixture();
        var first = await fixture.Cache.ComputeKeyAsync(fixture.Request, ScriptBuildCacheFixture.Token);
        File.SetLastWriteTimeUtc(fixture.Script, DateTime.UtcNow.AddDays(-10));
        Assert.Equal(first, await fixture.Cache.ComputeKeyAsync(fixture.Request, ScriptBuildCacheFixture.Token));
    }

    [Theory]
    [InlineData("workflow.cs")]
    [InlineData("schedule.json")]
    [InlineData("helper.cs")]
    [InlineData("Directory.Build.props")]
    [InlineData("Directory.Build.targets")]
    [InlineData("Directory.Packages.props")]
    [InlineData("packages.lock.json")]
    [InlineData("NuGet.Config")]
    public async Task ChangingOrRemovingLocalInputInvalidatesKey(string fileName)
    {
        using var fixture = new ScriptBuildCacheFixture();
        var file = Path.Combine(fixture.Source, fileName);
        if (fileName != "workflow.cs")
            File.WriteAllText(file, "one");
        var first = await fixture.Cache.ComputeKeyAsync(fixture.Request, ScriptBuildCacheFixture.Token);
        File.AppendAllText(file, "two");
        var changed = await fixture.Cache.ComputeKeyAsync(fixture.Request, ScriptBuildCacheFixture.Token);
        Assert.NotEqual(first, changed);
        if (fileName != "workflow.cs")
        {
            File.Delete(file);
            Assert.NotEqual(changed, await fixture.Cache.ComputeKeyAsync(fixture.Request, ScriptBuildCacheFixture.Token));
        }
    }

    [Fact]
    public async Task ExplicitDependencyTreeAndAncestorConfigurationAreTracked()
    {
        using var fixture = new ScriptBuildCacheFixture();
        var dependency = Path.Combine(fixture.Root, "dependency");
        Directory.CreateDirectory(dependency);
        var source = Path.Combine(dependency, "Sdk.cs");
        File.WriteAllText(source, "one");
        var request = fixture.Request with { AdditionalInputs = [dependency] };
        var first = await fixture.Cache.ComputeKeyAsync(request, ScriptBuildCacheFixture.Token);
        File.AppendAllText(source, "two");
        var second = await fixture.Cache.ComputeKeyAsync(request, ScriptBuildCacheFixture.Token);
        Assert.NotEqual(first, second);
        File.WriteAllText(Path.Combine(fixture.Root, "Directory.Build.props"), "<Project />");
        Assert.NotEqual(second, await fixture.Cache.ComputeKeyAsync(request, ScriptBuildCacheFixture.Token));
    }

    [Fact]
    public async Task OutputDirectoriesDoNotInvalidateKey()
    {
        using var fixture = new ScriptBuildCacheFixture();
        var nestedCache = Path.Combine(fixture.Source, "cache-output");
        var nestedArtifacts = Path.Combine(fixture.Source, "build-output");
        var cache = new ScriptBuildCache(nestedCache, nestedArtifacts);
        var first = await cache.ComputeKeyAsync(fixture.Request, ScriptBuildCacheFixture.Token);
        foreach (var directory in new[] { nestedCache, nestedArtifacts, Path.Combine(fixture.Source, "bin"),
                     Path.Combine(fixture.Source, "obj"), Path.Combine(fixture.Source, ".git") })
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "noise.cs"), "ignored");
        }
        Assert.Equal(first, await cache.ComputeKeyAsync(fixture.Request, ScriptBuildCacheFixture.Token));
    }

    [Fact]
    public async Task ScriptLocationAndAmbientMsBuildEnvironmentAreTracked()
    {
        using var fixture = new ScriptBuildCacheFixture();
        var first = await fixture.Cache.ComputeKeyAsync(fixture.Request, ScriptBuildCacheFixture.Token);
        var copy = Path.Combine(fixture.Source, "other.cs");
        File.Copy(fixture.Script, copy);
        Assert.NotEqual(first, await fixture.Cache.ComputeKeyAsync(fixture.Request with { ScriptPath = copy },
            ScriptBuildCacheFixture.Token));
        const string variable = "WF_SCRIPT_CACHE_KEY_TEST";
        var original = Environment.GetEnvironmentVariable(variable);
        try
        {
            var before = await fixture.Cache.ComputeKeyAsync(fixture.Request, ScriptBuildCacheFixture.Token);
            Environment.SetEnvironmentVariable(variable, Guid.NewGuid().ToString("N"));
            Assert.NotEqual(before, await fixture.Cache.ComputeKeyAsync(fixture.Request, ScriptBuildCacheFixture.Token));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, original);
        }
    }

    [Fact]
    public async Task MissingInputsAndScriptsOutsideTheDeclaredRootFail()
    {
        using var fixture = new ScriptBuildCacheFixture();
        await Assert.ThrowsAsync<FileNotFoundException>(() => fixture.Cache.ComputeKeyAsync(
            fixture.Request with { AdditionalInputs = [Path.Combine(fixture.Root, "missing")] }, ScriptBuildCacheFixture.Token));
        var outside = Path.Combine(fixture.Root, "outside.cs");
        File.Copy(fixture.Script, outside);
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Cache.ComputeKeyAsync(
            fixture.Request with { ScriptPath = outside }, ScriptBuildCacheFixture.Token));
    }

    [Fact]
    public async Task CancellationBeforeStartCreatesNoOutputs()
    {
        using var fixture = new ScriptBuildCacheFixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Cache.BuildAsync(fixture.Request, cancellation.Token));
        Assert.False(Directory.Exists(fixture.CacheDirectory));
    }
}
