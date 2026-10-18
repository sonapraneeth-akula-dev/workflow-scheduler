using System.Diagnostics;
using Wf.Sdk;

namespace Wf.Sdk.Tests;

public sealed class CommandTaskTests
{
    private static string Shell => OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";
    private static string[] ShellArguments(string script) => OperatingSystem.IsWindows()
        ? ["/d", "/s", "/c", script] : ["-c", script];
    private static string Exit(int code) => OperatingSystem.IsWindows() ? $"exit /b {code}" : $"exit {code}";

    [Fact]
    public async Task NonZeroExitNamesCodeAndHonorsRetryableCodeSet()
    {
        var context = new TaskContext(TestContext.Current.CancellationToken);
        var retryable = await Assert.ThrowsAsync<CommandTaskException>(() =>
            Tasks.Command(Shell, ShellArguments(Exit(3)), retryableExitCodes: [3])(context));
        Assert.Equal(3, retryable.ExitCode);
        Assert.Contains("3", retryable.Message);
        Assert.True(retryable.Retryable);
        var permanent = await Assert.ThrowsAsync<CommandTaskException>(() =>
            Tasks.Command(Shell, ShellArguments(Exit(3)), retryableExitCodes: [2])(context));
        Assert.False(permanent.Retryable);
    }

    [Fact]
    public async Task WorkingDirectoryAndEnvironmentAreUsedWithoutMutatingCallerEnvironment()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"wf-command-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var environment = new Dictionary<string, string?> { ["WF_TEST_VALUE"] = "hello world" };
            var script = OperatingSystem.IsWindows() ? "echo %WF_TEST_VALUE%> result.txt" :
                "printf '%s' \"$WF_TEST_VALUE\" > result.txt";
            var command = Tasks.Command(Shell, ShellArguments(script), directory, environment);
            environment["WF_TEST_VALUE"] = "changed";
            await command(new(TestContext.Current.CancellationToken));
            Assert.Equal("hello world", (await File.ReadAllTextAsync(Path.Combine(directory, "result.txt"),
                TestContext.Current.CancellationToken)).TrimEnd('\r', '\n'));
        }
        finally
        {
            File.Delete(Path.Combine(directory, "result.txt"));
            Directory.Delete(directory);
        }
    }

    [Fact]
    public async Task RawArgumentStringAndSafeArgumentListBothExecute()
    {
        var context = new TaskContext(TestContext.Current.CancellationToken);
        await Tasks.Command(Shell, OperatingSystem.IsWindows() ? "/d /s /c \"exit /b 0\"" : "-c \"exit 0\"")(context);
        await Tasks.Command(Shell, ShellArguments(Exit(0)))(context);
    }

    [Fact]
    public async Task CancellationStopsAndReapsTheProcessTree()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var directory = Path.Combine(Path.GetTempPath(), $"wf-cancel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var script = OperatingSystem.IsWindows()
                ? "echo started> started.txt & ping -n 31 127.0.0.1 > nul"
                : "printf started > started.txt; sleep 30";
            var task = Tasks.Command(Shell, ShellArguments(script), directory)(new(cancellation.Token));
            var stopwatch = Stopwatch.StartNew();
            while (!File.Exists(Path.Combine(directory, "started.txt")) && stopwatch.Elapsed < TimeSpan.FromSeconds(10))
            {
                Assert.False(task.IsCompleted, "Command exited before its start marker.");
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }
            Assert.True(File.Exists(Path.Combine(directory, "started.txt")));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10));
        }
        finally
        {
            cancellation.Cancel();
            File.Delete(Path.Combine(directory, "started.txt"));
            Directory.Delete(directory);
        }
    }

    [Fact]
    public async Task AlreadyCancelledDoesNotStartCommand()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Tasks.Command("definitely-not-an-executable")(new(cancellation.Token)));
    }

    [Fact]
    public void InvalidEnvironmentAndRetryableSuccessAreRejected()
    {
        Assert.Throws<ArgumentException>(() => Tasks.Command("tool", environment:
            new Dictionary<string, string?> { ["BAD=NAME"] = "value" }));
        Assert.Throws<ArgumentException>(() => Tasks.Command("tool", environment:
            new Dictionary<string, string?> { ["NAME"] = "bad\0value" }));
        Assert.Throws<ArgumentException>(() => Tasks.Command("tool", retryableExitCodes: [0]));
        Assert.Throws<ArgumentException>(() => Tasks.Command("tool", new string[] { null! }));
        if (OperatingSystem.IsWindows())
            Assert.Throws<ArgumentException>(() => Tasks.Command("tool", environment:
                new Dictionary<string, string?> { ["NAME"] = "one", ["name"] = "two" }));
    }

    [Fact]
    public async Task WorkflowResolvesCommandDelegateByStableKey()
    {
        var workflow = Workflow.Define("command", "1", w => w.Type("test")
            .Node("node", n => n.Task("command", Tasks.Command(Shell, ShellArguments(Exit(0))))));
        Assert.Equal("command", ManifestWriter.Create(workflow).Nodes[0].Tasks[0].Key);
        await workflow.Tasks["node/command"](new(TestContext.Current.CancellationToken));
    }
}
