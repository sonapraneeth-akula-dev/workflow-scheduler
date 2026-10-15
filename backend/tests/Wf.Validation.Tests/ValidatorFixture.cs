using System.Runtime.CompilerServices;
using Wf.Validation.Build;

namespace Wf.Validation.Tests;

internal sealed class ValidatorFixture : IDisposable
{
    internal const string ValidDefinition = """
        var workflow = Workflow.Define("validator/integration", "1.0.0", w => w
            .Type("release")
            .Variables(v => v.String("environment", "dev"))
            .Node("prepare", n => n.Task("work", _ =>
                throw new InvalidOperationException("Validation must not execute tasks.")))
            .Node("deploy", n => n.DependsOn("prepare").Task("work", _ =>
                throw new InvalidOperationException("Validation must not execute tasks."))));
        await ManifestWriter.WriteAsync(workflow, Console.OpenStandardOutput());
        """;

    internal string Root { get; } = Path.Combine(Repository, ".validator-tests", Guid.NewGuid().ToString("N"));
    internal string Source => Path.Combine(Root, "workflow");
    internal string Script => Path.Combine(Source, "workflow.cs");
    internal string CacheDirectory => Path.Combine(Root, "cache");
    internal string ArtifactsDirectory => Path.Combine(Root, "artifacts");
    internal ScriptBuildCache Cache => new(CacheDirectory, ArtifactsDirectory);
    internal static CancellationToken Token => TestContext.Current.CancellationToken;
    internal string[] AdditionalInputs { get; } =
        new[] { "Wf.Sdk", "Wf.Contracts", "Wf.Engine.Core", "Wf.Validation" }
            .Select(name => Path.Combine(Repository, "backend", "src", name)).ToArray();

    internal ValidatorFixture() => Directory.CreateDirectory(Source);

    internal void WriteWorkflow(string? body = null)
    {
        var sdk = Path.Combine(Repository, "backend", "src", "Wf.Sdk", "Wf.Sdk.csproj");
        File.WriteAllText(Script, $$"""
            #:project {{sdk}}
            #:property PublishAot=false
            using Wf.Sdk;

            if (!args.Contains("--describe"))
                throw new InvalidOperationException("Expected --describe.");
            {{body ?? ValidDefinition}}
            return 0;
            """);
    }

    internal static async Task WaitForFileAsync(string file, Task build)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        while (!File.Exists(file))
        {
            if (build.IsCompleted)
                await build;
            await Task.Delay(50, timeout.Token);
        }
    }

    private static string Repository => FindRepository();

    private static string FindRepository([CallerFilePath] string source = "")
    {
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(source)!);
             directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "backend", "src", "Wf.Sdk", "Wf.Sdk.csproj")))
                return directory.FullName;
        throw new DirectoryNotFoundException("Validator integration tests require the repository source tree.");
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
            Directory.Delete(Root, recursive: true);
    }
}

[CollectionDefinition("Validator", DisableParallelization = true)]
public sealed class ValidatorCollection;
