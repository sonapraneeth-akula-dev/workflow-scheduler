namespace Wf.Integration.Tests.Registration;

public sealed partial class PublishRegistrationTests
{
    private static async Task<string> PreparePackageConsumer(string root, string checkout)
    {
        var feed = Path.Combine(root, "f");
        Directory.CreateDirectory(feed);
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        foreach (var name in new[] { "Wf.Contracts", "Wf.Engine.Core", "Wf.Validation", "Wf.Sdk" })
        {
            var packed = await External("dotnet",
                ["pack", Path.Combine(Repository, "backend", "src", name, name + ".csproj"),
                    "--no-build", "--no-restore", "--configuration", configuration, "--output", feed, "--nologo"],
                Repository, null);
            Assert.True(packed.Exit == 0, packed.Output + packed.Error);
        }
        using var assets = System.Text.Json.JsonDocument.Parse(await File.ReadAllBytesAsync(
            Path.Combine(Repository, "backend", "src", "Wf.Sdk", "obj", "project.assets.json"), Token));
        var folders = assets.RootElement.GetProperty("packageFolders").EnumerateObject()
            .Select(folder => folder.Name).ToArray();
        // Existing package caches are read-only feeds; all consumer restores target this fixture's isolated cache.
        var sources = string.Join(Environment.NewLine, folders.Select((folder, index) =>
            $"""<add key="restored-{index}" value="{System.Security.SecurityElement.Escape(folder)}" />"""));
        var mappings = string.Join(Environment.NewLine, folders.Select((_, index) =>
            $"""<packageSource key="restored-{index}"><package pattern="*" /></packageSource>"""));
        await File.WriteAllTextAsync(Path.Combine(checkout, "NuGet.Config"), $"""
            <configuration>
              <packageSources>
                <clear />
                <add key="local" value="{System.Security.SecurityElement.Escape(feed)}" />
                {sources}
              </packageSources>
              <disabledPackageSources><clear /></disabledPackageSources>
              <packageSourceMapping>
                <clear />
                <packageSource key="local"><package pattern="Wf.*" /></packageSource>
                {mappings}
              </packageSourceMapping>
            </configuration>
            """, Token);
        await File.WriteAllTextAsync(Path.Combine(checkout, "Directory.Packages.props"), """
            <Project>
              <PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup>
              <ItemGroup><PackageVersion Include="Wf.Sdk" Version="1.0.0" /></ItemGroup>
            </Project>
            """, Token);
        // Match a consumer repository: published SDK packages, not an SDK source rebuild on every merge.
        return Path.Combine(root, "p");
    }

    [Fact]
    public async Task RegistrationLatency_twenty_real_main_merges_activate_within_sixty_seconds_p95()
    {
        var timings = await PublishMergedVersionsAsync(20);
        Assert.Equal(20, timings.Length);
        var sorted = timings.Select(timing => timing.MergeToActive).Order().ToArray();
        var p95 = sorted[(int)Math.Ceiling(sorted.Length * 0.95) - 1];
        output.WriteLine($"20 real merges: merge-to-active p95={p95.TotalMilliseconds:F1} ms; " +
            $"maximum registration-to-active upper bound={timings.Max(timing => timing.RegistrationToActive).TotalMilliseconds:F1} ms. " +
            "Local template publication includes validation/build/upload/registration and the production registry listener; " +
            "SDK package preparation, hosted runner queue, PR test execution and live Entra token exchange are not measured.");
        Assert.True(p95 <= TimeSpan.FromSeconds(60), $"Merge-to-active p95 {p95} exceeds sixty seconds.");
    }

    private sealed record MergeTiming(TimeSpan MergeToActive, TimeSpan RegistrationToActive);
}
