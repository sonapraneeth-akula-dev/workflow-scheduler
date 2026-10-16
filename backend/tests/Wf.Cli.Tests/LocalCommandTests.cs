using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using Wf.Cli.Commands;

namespace Wf.Cli.Tests;

public sealed class LocalCommandTests : IDisposable
{
    private readonly string root = Path.Combine(Repository, ".agent-artifacts", "compose-cli-validation", "cases",
        Guid.NewGuid().ToString("N") + " checkout ' #");
    private readonly List<ProcessStartInfo> calls = [];
    private string State(string env = "ppe") => Path.Combine(root, ".wf", "local", env);
    private string Runtime(string env = "ppe") => Path.Combine(State(env), "runtime.env");
    private string Compose(string env = "ppe") => Path.Combine(State(env), "publish", "docker-compose.yaml");
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public LocalCommandTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "backend", "src", "Wf.AppHost"));
        Directory.CreateDirectory(Path.Combine(root, "infra", "local"));
        File.WriteAllText(Path.Combine(root, "backend", "src", "Wf.AppHost", "Wf.AppHost.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(root, "infra", "local", "compose.override.yml"), "services: {}");
    }

    [Theory]
    [InlineData(null, "ppe", "127.2.6.7")]
    [InlineData("ppe", "ppe", "127.2.6.7")]
    [InlineData("dev", "dev", "127.2.6.2")]
    public async Task Up_uses_exact_native_commands_and_environment(string? option, string env, string bind)
    {
        var nested = Path.Combine(root, "directory with spaces ' and #", "nested");
        Directory.CreateDirectory(nested);
        var result = await Run(option is null ? ["up"] : ["up", "--env", option], cwd: nested);
        Assert.Equal(0, result.Exit);
        Assert.Empty(result.Error);
        Assert.Equal(3, calls.Count);
        Assert.Equal("aspire", calls[0].FileName);
        Assert.Equal(
            ["publish", "--apphost", Path.Combine(root, "backend", "src", "Wf.AppHost", "Wf.AppHost.csproj"),
             "--output-path", Path.Combine(State(env), "publish"), "--environment", env, "--non-interactive", "--nologo",
             "--", "--Local:Compose=true", "--Local:ComposeEnvironment=" + env],
            calls[0].ArgumentList);
        AssertCompose(calls[1], env, ["config", "--quiet"]);
        AssertCompose(calls[2], env, ["up", "-d", "--build", "--wait", "--wait-timeout", "180"]);
        var stable = ReadKeys(env);
        foreach (var call in calls)
        {
            Assert.False(call.UseShellExecute);
            Assert.True(call.RedirectStandardOutput);
            Assert.True(call.RedirectStandardError);
            Assert.Equal(root, call.WorkingDirectory);
            Assert.Empty(call.Arguments);
            Assert.Equal(env, call.Environment["KARDIA_ENVIRONMENT"]);
            Assert.Equal(bind, call.Environment["KARDIA_BIND_IP"]);
            Assert.Equal(env == "dev" ? "172.30.62.0/24" : "172.30.67.0/24", call.Environment["KARDIA_SUBNET"]);
            Assert.Equal(root, call.Environment["KARDIA_REPOSITORY_ROOT"]);
            foreach (var (key, value) in stable)
            {
                Assert.Equal(value, call.Environment[key]);
                Assert.DoesNotContain(value, string.Join(" ", call.ArgumentList));
                Assert.DoesNotContain(value, result.Output + result.Error);
            }
        }
        Assert.Contains("kardia-" + env, result.Output);
        Assert.Contains("http://" + bind + ":5000", result.Output);
        Assert.Contains("http://" + bind + ":18888", result.Output);
        Assert.DoesNotContain("login", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("up --env")]
    [InlineData("up --env DEV")]
    [InlineData("up --env Ppe")]
    [InlineData("up --env prod")]
    [InlineData("up --env dev --env dev")]
    [InlineData("up --env dev --env ppe")]
    [InlineData("up --repo never-echo")]
    [InlineData("up --config never-echo")]
    [InlineData("up --env=dev")]
    [InlineData("up extra")]
    [InlineData("down --volumes")]
    [InlineData("down --env ppe --remove-orphans")]
    [InlineData("UP")]
    [InlineData("--help --env dev")]
    [InlineData("up --env dev --help")]
    public async Task Invalid_arguments_fail_before_files_or_processes(string arguments)
    {
        var result = await Run(arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(2, result.Exit);
        Assert.Contains(LocalCommand.Usage, result.Error);
        Assert.Empty(result.Output);
        Assert.DoesNotContain("never-echo", result.Error);
        Assert.Empty(calls);
        Assert.False(Path.Exists(Path.Combine(root, ".wf")));
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("up --help")]
    [InlineData("up -h")]
    [InlineData("down --help")]
    [InlineData("down -h")]
    public async Task Help_needs_neither_checkout_nor_side_effects(string arguments)
    {
        var result = await Run(arguments.Split(' '), cwd: Path.Combine(root, "not-a-checkout"));
        Assert.Equal(0, result.Exit);
        Assert.Contains(LocalCommand.Usage, result.Output);
        Assert.Empty(result.Error);
        Assert.Empty(calls);
        Assert.False(Path.Exists(Path.Combine(root, ".wf")));
    }

    [Theory]
    [InlineData("--help", 0)]
    [InlineData("local --help", 0)]
    [InlineData("local up --help", 0)]
    [InlineData("local down -h", 0)]
    [InlineData("local down --volumes", 2)]
    [InlineData("local", 2)]
    public async Task Program_routes_local_and_root_help(string arguments, int expectedExit)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = root, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        foreach (var argument in arguments.Split(' ')) start.ArgumentList.Add(argument);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await LocalCommand.RunProcessAsync(start, output, error, [], Token);
        Assert.Equal(expectedExit, exit);
        Assert.Contains("Usage: wf local", output.ToString() + error);
        Assert.False(Path.Exists(Path.Combine(root, ".wf")));
    }

    [Fact]
    public async Task Absent_source_checkout_fails_before_state_or_processes()
    {
        // Fixture directories live inside the real checkout; start at the volume root to avoid discovering it.
        var volume = Path.GetPathRoot(root)!;
        Assert.False(File.Exists(Path.Combine(volume, "backend", "src", "Wf.AppHost", "Wf.AppHost.csproj")) &&
            File.Exists(Path.Combine(volume, "infra", "local", "compose.override.yml")));
        var result = await Run(["up"], cwd: volume);
        Assert.Equal(2, result.Exit);
        Assert.Contains("source checkout is required", result.Error);
        Assert.Empty(calls);
        Assert.False(Path.Exists(Path.Combine(root, ".wf")));
    }

    [Fact]
    public async Task Runtime_keys_are_private_valid_and_stable_across_restarts_and_down()
    {
        Assert.Equal(0, (await Run(["up"])).Exit);
        var original = await File.ReadAllBytesAsync(Runtime(), Token);
        var keys = ReadKeys();
        Assert.Equal(["POSTGRES_PASSWORD", "STORAGE_KEY", "AGENT_ID"], keys.Keys);
        Assert.Equal(64, keys["POSTGRES_PASSWORD"].Length);
        Assert.All(keys["POSTGRES_PASSWORD"], character => Assert.True(char.IsAsciiHexDigitLower(character)));
        Assert.Equal(32, Convert.FromBase64String(keys["STORAGE_KEY"]).Length);
        Assert.Equal(Guid.Parse(keys["AGENT_ID"]).ToString("D"), keys["AGENT_ID"]);
        Assert.NotEqual(Guid.Empty, Guid.Parse(keys["AGENT_ID"]));
        AssertPrivate(Path.Combine(root, ".wf"), true);
        AssertPrivate(Path.Combine(root, ".wf", "local"), true);
        AssertPrivate(State(), true);
        AssertPrivate(Path.Combine(State(), "publish"), true);
        AssertPrivate(Runtime(), false);
        AssertPrivate(Path.Combine(State(), "operation.lock"), false);
        var blob = Path.Combine(State(), "retained-blob");
        await File.WriteAllTextAsync(blob, "database-and-blob-sentinel", Token);
        calls.Clear();
        Assert.Equal(0, (await Run(["up"])).Exit);
        Assert.Equal(original, await File.ReadAllBytesAsync(Runtime(), Token));
        calls.Clear();
        Assert.Equal(0, (await Run(["down"])).Exit);
        AssertCompose(Assert.Single(calls), "ppe", ["down"]);
        Assert.Equal(original, await File.ReadAllBytesAsync(Runtime(), Token));
        Assert.True(File.Exists(Compose()));
        Assert.Equal("database-and-blob-sentinel", await File.ReadAllTextAsync(blob, Token));
        calls.Clear();
        Assert.Equal(0, (await Run(["down"])).Exit);
        AssertCompose(Assert.Single(calls), "ppe", ["down"]);
        Assert.Equal(original, await File.ReadAllBytesAsync(Runtime(), Token));
    }

    [Fact]
    public async Task Dev_and_ppe_have_isolated_keys_projects_and_state()
    {
        Assert.Equal(0, (await Run(["up", "--env", "dev"])).Exit);
        Assert.Equal(0, (await Run(["up", "--env", "ppe"])).Exit);
        Assert.All(ReadKeys("dev"), pair => Assert.NotEqual(pair.Value, ReadKeys("ppe")[pair.Key]));
        calls.Clear();
        Assert.Equal(0, (await Run(["down", "--env", "dev"])).Exit);
        AssertCompose(Assert.Single(calls), "dev", ["down"]);
        Assert.True(File.Exists(Runtime("ppe")));
        Assert.True(File.Exists(Compose("ppe")));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("duplicate")]
    [InlineData("missing")]
    [InlineData("unknown")]
    [InlineData("password")]
    [InlineData("storage")]
    [InlineData("uuid")]
    [InlineData("uppercase-uuid")]
    [InlineData("blank-line")]
    [InlineData("utf8")]
    [InlineData("oversized")]
    public async Task Corrupt_state_fails_closed_without_regenerating_keys(string corruption)
    {
        Assert.Equal(0, (await Run(["up"])).Exit);
        var text = await File.ReadAllTextAsync(Runtime(), Token);
        var lines = text.Split('\n');
        var damaged = corruption switch
        {
            "empty" => "",
            "duplicate" => text + lines[0] + "\n",
            "missing" => string.Join('\n', lines.Skip(1)),
            "unknown" => text + "UNKNOWN=never-echo\n",
            "password" => text.Replace(ReadKeys()["POSTGRES_PASSWORD"], "never-echo", StringComparison.Ordinal),
            "storage" => text.Replace(ReadKeys()["STORAGE_KEY"], new string('x', 44), StringComparison.Ordinal),
            "uuid" => text.Replace(ReadKeys()["AGENT_ID"], Guid.Empty.ToString("D"), StringComparison.Ordinal),
            "uppercase-uuid" => text.Replace(ReadKeys()["AGENT_ID"], "ABCDEF01-0000-4000-8000-000000000001", StringComparison.Ordinal),
            "blank-line" => text + "\n",
            "oversized" => new string('x', 1025),
            _ => text
        };
        await File.WriteAllBytesAsync(Runtime(), corruption == "utf8" ? [0xff, 0xfe] : System.Text.Encoding.UTF8.GetBytes(damaged), Token);
        var original = await File.ReadAllBytesAsync(Runtime(), Token);
        calls.Clear();
        foreach (var command in new[] { "up", "down" })
        {
            var result = await Run([command]);
            Assert.Equal(2, result.Exit);
            Assert.Contains("runtime.env", result.Error);
            Assert.DoesNotContain("never-echo", result.Error);
            Assert.Empty(calls);
            Assert.Equal(original, await File.ReadAllBytesAsync(Runtime(), Token));
        }
    }

    [Theory]
    [InlineData("state")]
    [InlineData("runtime")]
    [InlineData("compose")]
    [InlineData("empty-compose")]
    public async Task Down_requires_saved_state_and_never_publishes_or_creates_replacement_keys(string missing)
    {
        if (missing != "state") Assert.Equal(0, (await Run(["up"])).Exit);
        if (missing == "runtime") File.Delete(Runtime());
        if (missing == "compose") File.Delete(Compose());
        if (missing == "empty-compose") await File.WriteAllTextAsync(Compose(), "", Token);
        calls.Clear();
        var result = await Run(["down"]);
        Assert.Equal(2, result.Exit);
        Assert.Empty(result.Output);
        Assert.Empty(calls);
        if (missing == "state") Assert.False(Directory.Exists(State()));
        if (missing == "runtime") Assert.False(File.Exists(Runtime()));
    }

    [Theory]
    [InlineData(0, "Aspire publish")]
    [InlineData(1, "Docker Compose config")]
    [InlineData(2, "Docker Compose up")]
    public async Task Failed_stage_stops_sequence_and_never_reports_ready(int failedCall, string stage)
    {
        var result = await Run(["up"], runner: (start, token) =>
        {
            var index = calls.Count;
            Fake(start, token);
            return Task.FromResult(index == failedCall ? 37 : 0);
        });
        Assert.Equal(2, result.Exit);
        Assert.Equal(failedCall + 1, calls.Count);
        Assert.Contains(stage, result.Error);
        Assert.Contains("37", result.Error);
        Assert.DoesNotContain("is ready", result.Output);
        Assert.True(File.Exists(Runtime()));
    }

    [Fact]
    public async Task Missing_executable_and_failed_down_are_actionable_not_success()
    {
        var missing = await Run(["up"], runner: (_, _) => throw new System.ComponentModel.Win32Exception(2));
        Assert.Equal(2, missing.Exit);
        Assert.Contains("Aspire publish", missing.Error);
        Assert.DoesNotContain("is ready", missing.Output);
        Assert.Equal(0, (await Run(["up"])).Exit);
        var original = await File.ReadAllBytesAsync(Runtime(), Token);
        var down = await Run(["down"], runner: (_, _) => Task.FromResult(23));
        Assert.Equal(2, down.Exit);
        Assert.Contains("Docker Compose down", down.Error);
        Assert.Contains("23", down.Error);
        Assert.Empty(down.Output);
        Assert.Equal(original, await File.ReadAllBytesAsync(Runtime(), Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Successful_publish_must_create_fresh_nonempty_yaml(bool empty)
    {
        Assert.Equal(0, (await Run(["up"])).Exit);
        calls.Clear();
        var result = await Run(["up"], runner: (start, _) =>
        {
            calls.Add(start);
            if (empty) File.WriteAllText(Compose(), "");
            return Task.FromResult(0);
        });
        Assert.Equal(2, result.Exit);
        Assert.Single(calls);
        Assert.Contains("missing or empty", result.Error);
        Assert.DoesNotContain("is ready", result.Output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_or_cancelled_republish_retains_previous_compose_for_scoped_down(bool cancelled)
    {
        Assert.Equal(0, (await Run(["up"])).Exit);
        await File.WriteAllTextAsync(Compose(), "services: { saved: {} }", Token);
        var original = await File.ReadAllBytesAsync(Runtime(), Token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var failed = await Run(["up"], runner: async (start, token) =>
        {
            await Fake(start, token);
            if (cancelled)
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
            }
            return 37;
        }, token: cancellation.Token);
        Assert.Equal(cancelled ? 130 : 2, failed.Exit);
        Assert.Equal("services: { saved: {} }", await File.ReadAllTextAsync(Compose(), Token));
        Assert.Equal(original, await File.ReadAllBytesAsync(Runtime(), Token));
        Assert.Empty(Directory.GetFiles(State(), ".previous-compose-*"));
        calls.Clear();
        Assert.Equal(0, (await Run(["down"])).Exit);
        AssertCompose(Assert.Single(calls), "ppe", ["down"]);
    }

    [Fact]
    public async Task Precancelled_operation_has_no_side_effects()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var result = await Run(["up"], token: cancellation.Token);
        Assert.Equal(130, result.Exit);
        Assert.Contains("wf local down --env ppe", result.Error);
        Assert.Empty(result.Output);
        Assert.Empty(calls);
        Assert.False(Path.Exists(Path.Combine(root, ".wf")));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Cancellation_stops_sequence_preserves_state_and_reports_scoped_down(int cancelledCall)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var result = await Run(["up", "--env", "dev"], runner: async (start, token) =>
        {
            var index = calls.Count;
            await Fake(start, token);
            if (index == cancelledCall)
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
            }
            return 0;
        }, token: cancellation.Token);
        Assert.Equal(130, result.Exit);
        Assert.Equal(cancelledCall + 1, calls.Count);
        Assert.Empty(result.Output);
        Assert.Contains("wf local down --env dev", result.Error);
        Assert.True(File.Exists(Runtime("dev")));
    }

    [Theory]
    [InlineData("up")]
    [InlineData("down")]
    public async Task Concurrent_same_environment_is_rejected_while_other_environment_can_run(string contender)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Run(["up"], runner: async (start, token) =>
        {
            await Fake(start, token);
            if (start.FileName == "aspire")
            {
                entered.SetResult();
                await release.Task.WaitAsync(token);
            }
            return 0;
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(20), Token);
        try
        {
            var rejected = await Run([contender]);
            Assert.Equal(2, rejected.Exit);
            Assert.Contains("already locked", rejected.Error);
            Assert.Single(calls);
            Assert.Equal(0, (await Run(["up", "--env", "dev"])).Exit);
        }
        finally { release.TrySetResult(); }
        Assert.Equal(0, (await first).Exit);
        Assert.Equal(0, (await Run(["down"])).Exit);
    }

    [Theory]
    [InlineData("directory")]
    [InlineData("runtime")]
    [InlineData("compose")]
    [InlineData("dangling")]
    public async Task Reparse_points_fail_closed_before_processes(string kind)
    {
        Assert.Equal(0, (await Run(["up"])).Exit);
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(outside);
        var link = kind == "directory" ? Path.Combine(root, ".wf", "local", "ppe") :
            kind == "compose" ? Compose() : Runtime();
        if (kind == "directory")
        {
            Directory.Move(link, Path.Combine(outside, "saved"));
            if (OperatingSystem.IsWindows()) CreateJunction(link, Path.Combine(outside, "saved"));
            else Directory.CreateSymbolicLink(link, Path.Combine(outside, "saved"));
        }
        else
        {
            File.Delete(link);
            if (OperatingSystem.IsWindows())
            {
                // Junctions need no Developer Mode and exercise the same fail-closed reparse checks.
                CreateJunction(link, kind == "dangling" ? Path.Combine(outside, "missing") : outside);
            }
            else File.CreateSymbolicLink(link, kind == "dangling" ? Path.Combine(outside, "missing") : Path.Combine(outside, "saved"));
        }
        calls.Clear();
        try
        {
            var result = await Run(["up"]);
            Assert.Equal(2, result.Exit);
            Assert.Contains("symbolic links or reparse points", result.Error);
            Assert.Empty(calls);
        }
        finally
        {
            if (OperatingSystem.IsWindows() || kind == "directory") Directory.Delete(link);
            else File.Delete(link);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Insecure_existing_state_is_rejected_not_silently_repaired(bool directory)
    {
        Assert.Equal(0, (await Run(["up"])).Exit);
        var path = directory ? State() : Runtime();
        if (OperatingSystem.IsWindows())
        {
            FileSystemSecurity security = directory ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                FileSystemRights.Read, AccessControlType.Allow));
            if (directory) new DirectoryInfo(path).SetAccessControl((DirectorySecurity)security);
            else new FileInfo(path).SetAccessControl((FileSecurity)security);
        }
        else File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.OtherRead);
        calls.Clear();
        var result = await Run(["up"]);
        Assert.Equal(2, result.Exit);
        Assert.Contains("permissions", result.Error);
        Assert.Empty(calls);
    }

    private async Task<(int Exit, string Output, string Error)> Run(string[] arguments, string? cwd = null,
        Func<ProcessStartInfo, CancellationToken, Task<int>>? runner = null, CancellationToken? token = null)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await LocalCommand.RunAsync(arguments, output, error, cwd ?? root, runner ?? Fake, token ?? Token);
        return (exit, output.ToString(), error.ToString());
    }

    private Task<int> Fake(ProcessStartInfo start, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        calls.Add(start);
        if (start.FileName == "aspire")
        {
            var publish = start.ArgumentList[start.ArgumentList.IndexOf("--output-path") + 1];
            File.WriteAllText(Path.Combine(publish, "docker-compose.yaml"), "services: {}");
        }
        return Task.FromResult(0);
    }

    private Dictionary<string, string> ReadKeys(string env = "ppe") => File.ReadAllLines(Runtime(env))
        .Select(line => line.Split('=', 2)).ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);

    private void AssertCompose(ProcessStartInfo start, string env, string[] operation)
    {
        Assert.Equal("docker", start.FileName);
        Assert.Equal(["compose", "--project-directory", root, "--env-file", Runtime(env), "-p", "kardia-" + env,
            "-f", Compose(env), "-f", Path.Combine(root, "infra", "local", "compose.override.yml"), .. operation], start.ArgumentList);
        Assert.DoesNotContain("--volumes", start.ArgumentList);
        Assert.DoesNotContain("--remove-orphans", start.ArgumentList);
        Assert.DoesNotContain("--rmi", start.ArgumentList);
        Assert.DoesNotContain("prune", start.ArgumentList);
    }

    private static void AssertPrivate(string path, bool directory)
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            FileSystemSecurity security = directory ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
            Assert.Equal(identity.User, security.GetOwner(typeof(SecurityIdentifier)));
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                Assert.Equal(identity.User, rule.IdentityReference);
        }
        else Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | (directory ? UnixFileMode.UserExecute : 0),
            File.GetUnixFileMode(path));
    }

    private static void CreateJunction(string link, string target)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec")!)
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    private static string Repository
    {
        get
        {
            for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
                if (File.Exists(Path.Combine(current.FullName, "backend", "src", "Wf.Cli", "Wf.Cli.csproj")))
                    return current.FullName;
            throw new InvalidOperationException("Source repository not found.");
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
