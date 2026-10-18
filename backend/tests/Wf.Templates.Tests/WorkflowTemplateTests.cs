using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Wf.Contracts.Manifest;
using Wf.Validation.Schedules;
using Wf.Validation.SemVer;

namespace Wf.Templates.Tests;

public sealed class WorkflowTemplateTests(ITestOutputHelper output)
{
    [Fact]
    public async Task WorkflowTemplatePackageInstallsGeneratesAndRunsProductionWorkflow()
    {
        var repository = FindRepository();
        var root = Path.Combine(repository, ".agent-artifacts", "tests", "templates", Guid.NewGuid().ToString("N")[..8]);
        var feed = Path.Combine(root, "f");
        var hive = Path.Combine(root, "h");
        var generated = Path.Combine(root, "g");
        Directory.CreateDirectory(feed);
        try
        {
            var preparation = Stopwatch.StartNew();
            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
            foreach (var name in new[] { "Wf.Contracts", "Wf.Engine.Core", "Wf.Validation", "Wf.Sdk", "Wf.Sdk.Testing" })
                await DotnetAsync(repository, null, "pack",
                    Path.Combine(repository, "backend", "src", name, name + ".csproj"),
                    "--no-build", "--no-restore", "--configuration", configuration,
                    "--output", feed, "--nologo");
            await DotnetAsync(repository, null, "pack",
                Path.Combine(repository, "backend", "src", "Wf.Templates", "Wf.Templates.csproj"),
                "--configuration", configuration, "--output", feed, "--nologo");
            CopyRestoredDependencies(repository, feed);
            output.WriteLine($"Package preparation (outside generated dotnet test): {preparation.Elapsed.TotalSeconds:F3}s");

            await DotnetAsync(repository, null, "new", "install", Path.Combine(feed, "Wf.Templates.1.0.0.nupkg"),
                "--debug:custom-hive", hive);
            var listed = await DotnetAsync(repository, null, "new", "list", "wf-workflow",
                "--debug:custom-hive", hive);
            Assert.Contains("wf-workflow", listed.Text);
            await DotnetAsync(repository, null, "new", "wf-workflow", "--name", "TemplateSmoke",
                "--sdkVersion", "1.0.0", "--output", generated, "--debug:custom-hive", hive);
            foreach (var file in new[] { "workflow.cs", "schedules.json", "workflow.api.json", "TemplateSmoke.Tests.csproj",
                         "Tests/WorkflowTests.cs", "schemas/schedules.schema.json", "Directory.Packages.props" })
                Assert.True(File.Exists(Path.Combine(generated, file.Replace('/', Path.DirectorySeparatorChar))), file);
            Assert.False(Directory.Exists(Path.Combine(generated, ".template.config")));
            var script = await File.ReadAllTextAsync(Path.Combine(generated, "workflow.cs"),
                TestContext.Current.CancellationToken);
            Assert.Contains("#:package Wf.Sdk", script);
            Assert.DoesNotContain("#:package Wf.Sdk@", script);
            Assert.Contains("#:property PublishAot=false", script);
            Assert.Contains("Workflow.Define(\"TemplateSmoke\"", script);
            Assert.DoesNotContain("#:project", script);
            Assert.DoesNotContain("SDK_PACKAGE_VERSION", await File.ReadAllTextAsync(
                Path.Combine(generated, "Directory.Packages.props"), TestContext.Current.CancellationToken));

            // Never populate the user's global NuGet cache with development Wf.* packages.
            var packages = Path.Combine(root, "p");
            await File.WriteAllTextAsync(Path.Combine(generated, "NuGet.Config"), $"""
                <configuration>
                  <packageSources>
                    <clear />
                    <add key="local" value="{System.Security.SecurityElement.Escape(feed)}" />
                  </packageSources>
                  <disabledPackageSources><clear /></disabledPackageSources>
                  <packageSourceMapping><clear /></packageSourceMapping>
                </configuration>
                """, TestContext.Current.CancellationToken);
            var cold = await DotnetAsync(generated, packages, "test");
            output.WriteLine($"Generated dotnet test COLD: {cold.Elapsed.TotalSeconds:F3}s " +
                "(empty isolated NuGet cache, prepared offline feed; includes restore, test build, file-app publish and tests).");
            Assert.Matches(@"succeeded:\s+4\b", cold.Text);
            var coldEntries = Directory.GetDirectories(Path.Combine(generated, "bin", "workflow-cache")).Length;
            Assert.Equal(1, coldEntries);
            var warm = await DotnetAsync(generated, packages, "test");
            output.WriteLine($"Generated dotnet test WARM: {warm.Elapsed.TotalSeconds:F3}s " +
                "(same directory/packages; still includes actual restore/build/publish-cache decision and tests).");
            output.WriteLine("Warm command profile:" + Environment.NewLine + warm.Text);
            Assert.Matches(@"succeeded:\s+4\b", warm.Text);
            var warmEntries = Directory.GetDirectories(Path.Combine(generated, "bin", "workflow-cache")).Length;
            output.WriteLine($"Workflow content-hash cache entries: after cold={coldEntries}, after warm={warmEntries}; " +
                $"warm published new workflow={warmEntries > coldEntries}.");
            output.WriteLine($"<10s acceptance: cold={cold.Elapsed < TimeSpan.FromSeconds(10)}, " +
                $"warm={warm.Elapsed < TimeSpan.FromSeconds(10)}. No Linux execution claimed.");
            VerifyPublishedContracts(generated);
            Assert.Equal(coldEntries, warmEntries);
            Assert.True(warm.Elapsed < TimeSpan.FromSeconds(10),
                $"Complete warm dotnet test exceeded 10s: {warm.Elapsed.TotalSeconds:F3}s.");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static void CopyRestoredDependencies(string repository, string feed)
    {
        using var assets = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(repository, "backend", "tests",
            "Wf.Templates.Tests", "obj", "project.assets.json")));
        var folders = assets.RootElement.GetProperty("packageFolders").EnumerateObject()
            .Select(folder => folder.Name).ToArray();
        foreach (var library in assets.RootElement.GetProperty("libraries").EnumerateObject()
                     .Where(library => library.Value.GetProperty("type").GetString() == "package"))
        {
            var relative = library.Value.GetProperty("path").GetString()!.Replace('/', Path.DirectorySeparatorChar);
            var name = library.Name.Replace('/', '.').ToLowerInvariant() + ".nupkg";
            var source = folders.Select(folder => Path.Combine(folder, relative, name)).First(File.Exists);
            File.Copy(source, Path.Combine(feed, name));
        }
    }

    private static void VerifyPublishedContracts(string generated)
    {
        var manifestFile = Directory.EnumerateFiles(Path.Combine(generated, "bin", "workflow-cache"),
            "manifest.json", SearchOption.AllDirectories).First();
        var manifest = JsonSerializer.Deserialize(File.ReadAllBytes(manifestFile),
            WorkflowManifestJsonContext.Default.WorkflowManifest)!;
        var baseline = ApiBaseline.Parse(File.ReadAllBytes(Path.Combine(generated, ApiBaseline.FileName)));
        Assert.Equal(ApiBaseline.FromManifest(manifest).WriteUtf8(), baseline.WriteUtf8());
        var schedules = JsonNode.Parse(File.ReadAllBytes(Path.Combine(generated, "schedules.json")))!;
        var valid = ScheduleFileValidator.Validate(JsonSerializer.SerializeToUtf8Bytes(schedules), manifest.Variables);
        Assert.True(valid.IsValid, string.Join(Environment.NewLine, valid.Errors));
        Assert.Equal(new[] { "daily-dev", "ppe-check" }, valid.Instances.Select(instance => instance.Definition.Id));
        Assert.Equal("dev", valid.Instances[0].Variables["env"].GetString());
        Assert.Equal("ppe", valid.Instances[1].Variables["env"].GetString());
        schedules["instances"]![0]!["interval"] = "00:15:00";
        Assert.False(ScheduleFileValidator.Validate(
            JsonSerializer.SerializeToUtf8Bytes(schedules), manifest.Variables).IsValid);
        schedules["instances"]![0]!.AsObject().Remove("interval");
        schedules["instances"]![1]!["variables"]!["env"] = "not-allowed";
        Assert.False(ScheduleFileValidator.Validate(
            JsonSerializer.SerializeToUtf8Bytes(schedules), manifest.Variables).IsValid);
    }

    private static async Task<CommandResult> DotnetAsync(string workingDirectory, string? packages,
        params string[] arguments)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(4));
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1";
        if (packages is not null)
            start.Environment["NUGET_PACKAGES"] = packages;
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        var timer = Stopwatch.StartNew();
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(stdout, stderr);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) when (process.HasExited) { }
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        var text = await stdout + Environment.NewLine + await stderr;
        Assert.True(process.ExitCode == 0, $"dotnet {string.Join(' ', arguments)}{Environment.NewLine}{text}");
        return new(text, timer.Elapsed);
    }

    private static string FindRepository([CallerFilePath] string file = "")
    {
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(file)!); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "backend", "src", "Wf.Templates", "Wf.Templates.csproj")))
                return directory.FullName;
        throw new DirectoryNotFoundException("Template integration tests require the repository source tree.");
    }

    private sealed record CommandResult(string Text, TimeSpan Elapsed);
}
