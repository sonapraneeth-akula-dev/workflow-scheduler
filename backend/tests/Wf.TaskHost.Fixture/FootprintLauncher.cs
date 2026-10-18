using System.Diagnostics;

namespace Wf.TaskHost.Fixture;

public sealed class FootprintLauncher : IDisposable
{
    private readonly string root = Path.GetFullPath(Path.Combine(".agent-artifacts", "footprint-launcher", Guid.NewGuid().ToString("N")));
    public string EntryAssembly { get; }

    public FootprintLauncher()
    {
        Directory.CreateDirectory(root);
        EntryAssembly = Path.Combine(root, "footprint-host.dll");
        try
        {
            // Compile only an entry point with the installed SDK; no extra project or restore.
            var sdk = Run(["--list-sdks"]).Trim().Split('\n').Last().Trim();
            var compiler = Path.Combine(sdk[(sdk.IndexOf('[') + 1)..sdk.LastIndexOf(']')],
                sdk[..sdk.IndexOf(' ')], "Roslyn", "bincore", "csc.dll");
            var source = Path.Combine(root, "Entry.cs");
            File.WriteAllText(source, "public static class Entry { public static async System.Threading.Tasks.Task Main(string[] args) => await Wf.TaskHost.Fixture.FootprintHost.RunAsync(args); }");
            var response = Path.Combine(root, "compile.rsp");
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Where(path => Path.GetFileName(path) is "System.Private.CoreLib.dll" or "System.Runtime.dll" or "System.Threading.Tasks.dll");
            File.WriteAllLines(response, new[] { "/nologo", "/target:exe", $"/out:\"{EntryAssembly}\"", $"\"{source}\"",
                $"/reference:\"{typeof(FixtureMarker).Assembly.Location}\"" }
                .Concat(references.Select(path => $"/reference:\"{path}\"")));
            Run([compiler, "@" + response]);
        }
        catch { Dispose(); throw; }
    }

    private static string Run(string[] arguments)
    {
        var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new TimeoutException("SDK compiler did not exit.");
        }
        if (process.ExitCode != 0) throw new InvalidOperationException(stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
        return stdout.GetAwaiter().GetResult();
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
}
