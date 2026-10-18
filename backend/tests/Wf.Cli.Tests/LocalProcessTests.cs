using System.Diagnostics;
using Wf.Cli.Commands;

namespace Wf.Cli.Tests;

public sealed class LocalProcessTests
{
    [Fact]
    public async Task Process_forwards_both_streams_redacts_secrets_and_returns_real_exit()
    {
        var start = Shell(OperatingSystem.IsWindows()
            ? "1..150 | ForEach-Object { [Console]::Out.WriteLine($env:LOCAL_TEST_SECRET); [Console]::Error.WriteLine('diagnostic') }; exit 37"
            : "i=0; while [ \"$i\" -lt 150 ]; do echo \"$LOCAL_TEST_SECRET\"; echo diagnostic >&2; i=$((i+1)); done; exit 37");
        start.Environment["LOCAL_TEST_SECRET"] = "private-value-never-log";
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await LocalCommand.RunProcessAsync(start, output, error, ["private-value-never-log"], TestContext.Current.CancellationToken);
        Assert.Equal(37, exit);
        Assert.Equal(150, output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Equal(150, error.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Contains("[REDACTED]", output.ToString());
        Assert.DoesNotContain("private-value-never-log", output.ToString() + error);
        Assert.Contains("diagnostic", error.ToString());
    }

    [Fact]
    public async Task Cancellation_kills_only_owned_process_tree_and_never_unrelated_process()
    {
        using var unrelated = Process.Start(Shell(OperatingSystem.IsWindows() ? "Start-Sleep -Seconds 60" : "sleep 60"))!;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var output = new PidWriter();
        using var error = new StringWriter();
        var start = Shell(OperatingSystem.IsWindows()
            ? "$child = Start-Process -FilePath (Join-Path $PSHOME 'powershell.exe') -ArgumentList '-NoProfile','-NonInteractive','-Command','Start-Sleep -Seconds 60' -PassThru; [Console]::Out.WriteLine($PID); [Console]::Out.WriteLine($child.Id); Start-Sleep -Seconds 60"
            : "echo $$; sleep 60 & echo $!; wait");
        var run = LocalCommand.RunProcessAsync(start, output, error, [], cancellation.Token);
        int[] ids = [];
        try
        {
            ids = await output.Pids.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                run.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken));
            Assert.All(ids, id => Assert.True(Exited(id), $"Owned process {id} still running."));
            Assert.False(unrelated.HasExited);
        }
        finally
        {
            cancellation.Cancel();
            try { await run; }
            catch (OperationCanceledException) { }
            foreach (var id in ids)
                if (!Exited(id))
                {
                    using var owned = Process.GetProcessById(id);
                    owned.Kill(entireProcessTree: true);
                }
            if (!unrelated.HasExited) unrelated.Kill(entireProcessTree: true);
            await unrelated.WaitForExitAsync(CancellationToken.None);
        }
    }

    private static bool Exited(int id)
    {
        try
        {
            using var process = Process.GetProcessById(id);
            return process.HasExited;
        }
        catch (ArgumentException) { return true; }
    }

    private static ProcessStartInfo Shell(string command)
    {
        var start = new ProcessStartInfo(OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe")
            : "/bin/sh")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in OperatingSystem.IsWindows()
            ? new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", command } : new[] { "-c", command })
            start.ArgumentList.Add(argument);
        return start;
    }

    private sealed class PidWriter : StringWriter
    {
        private readonly List<int> ids = [];
        internal TaskCompletionSource<int[]> Pids { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Task WriteLineAsync(string? value)
        {
            if (int.TryParse(value, out var id))
            {
                ids.Add(id);
                if (ids.Count == 2) Pids.TrySetResult(ids.ToArray());
            }
            return base.WriteLineAsync(value);
        }
    }
}
