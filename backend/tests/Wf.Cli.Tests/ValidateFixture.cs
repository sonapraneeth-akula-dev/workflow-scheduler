using System.Diagnostics;
using System.Text;
using Wf.Cli.Commands;
using Wf.Validation.Build;

namespace Wf.Cli.Tests;

[CollectionDefinition("Validate", DisableParallelization = true)]
public sealed class ValidateCollection : ICollectionFixture<ValidateFixture>;

public sealed class ValidateFixture : IAsyncLifetime
{
    internal string Root { get; } = Path.Combine(Repository, ".wc", Guid.NewGuid().ToString("N")[..8]);
    internal string Source => Path.Combine(Root, "s");
    internal string Work => Path.Combine(Root, "w");
    internal ScriptBuildCache Cache => new(Path.Combine(Work, "c"), Path.Combine(Work, "a"));
    internal string[] Inputs { get; } =
        ["Wf.Sdk", "Wf.Contracts", "Wf.Engine.Core", "Wf.Validation"];
    internal string[] Closure => Inputs.Select(name => Path.Combine(Repository, "backend", "src", name))
        .Append(Path.Combine(Repository, "backend", "proto")).ToArray();
    internal ScriptBuildResult Build { get; private set; } = null!;
    internal static CancellationToken Token => TestContext.Current.CancellationToken;
    private readonly Dictionary<string, string?> environment = new();

    public async ValueTask InitializeAsync()
    {
        // Match the dedicated wf process's compilation defaults for independent cache-miss builds.
        foreach (var (name, value) in new[] { ("Deterministic", "true"), ("DebugType", "none"), ("DebugSymbols", "false") })
        {
            environment[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }
        Directory.CreateDirectory(Source);
        File.WriteAllText(Path.Combine(Source, "workflow.cs"), $$"""
            #:project {{Path.Combine(Repository, "backend", "src", "Wf.Sdk", "Wf.Sdk.csproj")}}
            #:property PublishAot=false
            #:property AssemblyName=Custom.Validate.Workflow
            using Wf.Sdk;
            var workflow = Workflow.Define("tests/cli/validate", "1.0.0", w => w.Type("test")
                .Node("work", n => n.Task("echo", ctx => ctx.Outputs["answer"] = 42)));
            return await workflow.RunAsync(args);
            """);
        Build = await Cache.BuildAsync(new(Path.Combine(Source, "workflow.cs"), Source, Closure), Token);
    }

    internal string NewDirectory()
    {
        var path = Path.Combine(Root, Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(path);
        return path;
    }

    internal string[] Arguments(params string[] extra) => new[] { Source, "--work-directory", Work }
        .Concat(Closure.SelectMany(path => new[] { "--input", path })).Concat(extra).ToArray();

    internal static async Task<(int Exit, string Output, string Error)> RunAsync(string[] arguments,
        CancellationToken token = default)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await ValidateCommand.RunAsync(arguments, output, error, token);
        return (exit, output.ToString(), error.ToString());
    }

    internal static async Task<(int Exit, string Output, string Error)> ProcessAsync(string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        foreach (var name in new[] { "Deterministic", "DebugType", "DebugSymbols" })
            start.Environment.Remove(name);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(Token);
        var error = process.StandardError.ReadToEndAsync(Token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        return (process.ExitCode, await output, await error);
    }

    internal ScriptBuildResult SyntheticPublish(params (string Name, string Content)[] extra)
    {
        var root = NewDirectory();
        File.WriteAllText(Path.Combine(root, "custom.dll"), "assembly");
        foreach (var (name, content) in extra)
        {
            var path = Path.Combine(root, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content, new UTF8Encoding(false));
        }
        return Build with { PublishDirectory = root, AssemblyPath = Path.Combine(root, "custom.dll") };
    }

    public ValueTask DisposeAsync()
    {
        foreach (var (name, value) in environment) Environment.SetEnvironmentVariable(name, value);
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        var parent = Path.GetDirectoryName(Root)!;
        if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
            Directory.Delete(parent);
        return ValueTask.CompletedTask;
    }

    private static string Repository
    {
        get
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
                if (Directory.Exists(Path.Combine(directory.FullName, "backend", "proto")))
                    return directory.FullName;
            throw new DirectoryNotFoundException("CLI integration tests require the repository source tree.");
        }
    }
}
