using System.Diagnostics;
using Wf.Validation.Build;

namespace Wf.Validation.Tests.Build;

internal sealed class ScriptBuildCacheFixture : IDisposable
{
    internal const string DescribeBody = """
        using var schedule = JsonDocument.Parse(File.ReadAllText("schedule.json"));
        var cron = schedule.RootElement.GetProperty("cron").GetString()!;
        var workflow = Workflow.Define("spike/representative", "1", w =>
        {
            w.Type("release").Metadata("cron", cron)
                .Variables(v => v.String("environment", "dev"));
            for (var index = 0; index < 20; index++)
            {
                var node = index;
                w.Node($"node-{node}", n =>
                {
                    if (node > 0) n.DependsOn($"node-{node - 1}");
                    n.Task("work", _ => throw new InvalidOperationException("Describe must not run tasks."));
                });
            }
        });
        await ManifestWriter.WriteAsync(workflow, Console.OpenStandardOutput());
        """;

    internal string Root { get; } = Path.Combine(
        Environment.GetEnvironmentVariable("WF_SCRIPT_BUILD_TEST_DIRECTORY") ??
        Path.Combine(FindRepository(), ".script-build-cache-tests"), Guid.NewGuid().ToString("N"));
    internal string Source => Path.Combine(Root, "source");
    internal string Script => Path.Combine(Source, "workflow.cs");
    internal string CacheDirectory => Path.Combine(Root, "cache");
    internal string ArtifactsDirectory => Path.Combine(Root, "artifacts");
    internal ScriptBuildCache Cache => new(CacheDirectory, ArtifactsDirectory);
    internal ScriptBuildRequest Request => new(Script, Source);
    internal static CancellationToken Token => TestContext.Current.CancellationToken;

    internal ScriptBuildCacheFixture()
    {
        Directory.CreateDirectory(Source);
        File.WriteAllText(Script, "Console.WriteLine(\"key-only fixture\");");
    }

    internal ScriptBuildRequest WriteWorkflow(string? describeBody = null)
    {
        var repository = FindRepository();
        var sdk = Path.Combine(repository, "backend", "src", "Wf.Sdk", "Wf.Sdk.csproj");
        File.WriteAllText(Path.Combine(Source, "schedule.json"), """{"cron":"0 9 * * *","timeZone":"America/New_York"}""");
        File.WriteAllText(Script, $$"""
            #:project {{sdk}}
            #:property PublishAot=false
            using System.Diagnostics;
            using System.Text.Json;
            using Wf.Sdk;

            if (args.Contains("--child"))
            {
                await Task.Delay(Timeout.Infinite);
                return 0;
            }
            if (!args.Contains("--describe"))
                throw new InvalidOperationException("Expected --describe.");
            {{describeBody ?? DescribeBody}}
            return 0;
            """);
        return new(Script, Source,
            new[] { "Wf.Sdk", "Wf.Contracts", "Wf.Engine.Core", "Wf.Validation" }
            .Select(name => Path.Combine(repository, "backend", "src", name)).ToArray());
    }

    private static string FindRepository([System.Runtime.CompilerServices.CallerFilePath] string file = "")
    {
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(file)!); directory is not null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, "backend", "src", "Wf.Sdk")))
                return directory.FullName;
        throw new DirectoryNotFoundException("ScriptBuildCache integration tests require their repository source tree.");
    }

    internal static async Task WaitForFileAsync(string file, Task build)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        while (!File.Exists(file))
        {
            if (build.IsCompleted)
                await build;
            await Task.Delay(50, timeout.Token);
        }
    }

    internal static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    internal async Task<byte[]> RunDotnetAsync(params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Source,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        using var output = new MemoryStream();
        var readOutput = process.StandardOutput.BaseStream.CopyToAsync(output);
        var readError = process.StandardError.ReadToEndAsync(Token);
        try
        {
            await process.WaitForExitAsync(Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await readOutput;
            throw;
        }
        await readOutput;
        var error = await readError;
        Assert.True(process.ExitCode == 0, $"{error}{Environment.NewLine}{System.Text.Encoding.UTF8.GetString(output.ToArray())}");
        return output.ToArray();
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
            Directory.Delete(Root, recursive: true);
    }
}

[CollectionDefinition("ScriptBuildCache", DisableParallelization = true)]
public sealed class ScriptBuildCacheCollection;
