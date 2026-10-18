using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Wf.Templates.Tests;

[CollectionDefinition("Repository PR gate", DisableParallelization = true)]
public sealed class RepositoryPrGateCollection;

[Collection("Repository PR gate")]
public sealed class RepositoryPrGateTests(ITestOutputHelper output)
{
    [Fact]
    public async Task PrGateSelectsCompleteCommitRangeAndEveryContainingWorkflow()
    {
        using var sample = new Repository();
        await sample.InitializeAsync();
        foreach (var directory in new[] { "alpha", "alpha/nested", "beta" })
            sample.Write(directory + "/workflow.cs", "// selection fixture");
        var baseline = await sample.CommitAsync();
        sample.Write("alpha/first ' & [δ] $(echo nope).txt", "first commit");
        await sample.CommitAsync();
        sample.Write("beta/last.txt", "last commit");
        var head = await sample.CommitAsync();
        Assert.Equal(["alpha", "beta"], await sample.SelectedAsync(baseline, head));

        sample.Write("alpha/nested/Tests/changed.cs", "// nested workflow input");
        var nested = await sample.CommitAsync();
        Assert.Equal(["alpha", "alpha/nested"], await sample.SelectedAsync(head, nested));
        Assert.Empty(await sample.SelectedAsync(nested, nested));
        Assert.False(File.Exists(Path.Combine(sample.Path, "nope")));
    }

    [Fact]
    public async Task PrGateSelectsBothRenameSidesAndHandlesDeletedWorkflowsAndInputs()
    {
        using var sample = new Repository();
        await sample.InitializeAsync();
        sample.Write("alpha/workflow.cs", "// a");
        sample.Write("beta/workflow.cs", "// b");
        var special = OperatingSystem.IsWindows() ? "old ' & [δ] $(echo nope).txt" : "old '\n & [δ] $(echo nope).txt";
        sample.Write("alpha/" + special, "renamed input");
        var baseline = await sample.CommitAsync();
        await sample.GitAsync("mv", "--", "alpha/" + special, "beta/new ' & [δ].txt");
        var renamed = await sample.CommitAsync();
        Assert.Equal(["alpha", "beta"], await sample.SelectedAsync(baseline, renamed));
        File.Delete(sample.File("beta/new ' & [δ].txt"));
        var deleted = await sample.CommitAsync();
        Assert.Equal(["beta"], await sample.SelectedAsync(renamed, deleted));
        await sample.GitAsync("mv", "--", "alpha", "renamed ' & δ");
        var directoryRename = await sample.CommitAsync();
        Assert.Equal(["beta", "renamed ' & δ"], await sample.SelectedAsync(deleted, directoryRename));
        Directory.Delete(sample.File("renamed ' & δ"), recursive: true);
        var directoryDelete = await sample.CommitAsync();
        Assert.Equal(["beta"], await sample.SelectedAsync(directoryRename, directoryDelete));
        Directory.Delete(sample.File("beta"), recursive: true);
        var empty = await sample.CommitAsync();
        var result = await sample.GateAsync(directoryDelete, empty);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Affected workflows: []", result.Text);
        Assert.DoesNotContain("\"dotnet\"", result.Text);
    }

    [Fact]
    public async Task PrGateRechecksAllForTopLevelAndSharedDependenciesIncludingDeletions()
    {
        using var sample = new Repository();
        await sample.InitializeAsync();
        sample.Write("alpha/workflow.cs", "// a");
        sample.Write("beta/workflow.cs", "// b");
        sample.Write("shared/import.props", "<Project />");
        var baseline = await sample.CommitAsync();
        sample.Write("Directory.Build.props", "<Project />");
        var topLevel = await sample.CommitAsync();
        Assert.Equal(["alpha", "beta"], await sample.SelectedAsync(baseline, topLevel));
        File.Delete(sample.File("shared/import.props"));
        var sharedDelete = await sample.CommitAsync();
        Assert.Equal(["alpha", "beta"], await sample.SelectedAsync(topLevel, sharedDelete));
        sample.Write("docs/notes.txt", "Unknown inputs conservatively affect every workflow.");
        var documentation = await sample.CommitAsync();
        Assert.Equal(["alpha", "beta"], await sample.SelectedAsync(sharedDelete, documentation));
    }

    [Fact]
    public async Task PrGateIncludesCheckedOutMergeChangesNotOnlyPrHead()
    {
        using var sample = new Repository();
        await sample.InitializeAsync();
        sample.Write("alpha/workflow.cs", "// a");
        sample.Write("beta/workflow.cs", "// b");
        await sample.CommitAsync();
        await sample.GitAsync("checkout", "-q", "-b", "feat/sample");
        sample.Write("alpha/input.txt", "PR input");
        var head = await sample.CommitAsync();
        await sample.GitAsync("checkout", "-q", "main");
        sample.Write("beta/base.txt", "new base input");
        var baseline = await sample.CommitAsync();
        await sample.GitAsync("merge", "--quiet", "--no-ff", "--no-commit", "feat/sample");
        sample.Write("beta/merge-resolution.txt", "checked-out merge input");
        sample.Write("gamma/workflow.cs", "// added in checked-out merge, absent from PR head");
        var merge = await sample.CommitAsync();
        Assert.Equal(["alpha", "beta", "gamma"], await sample.SelectedAsync(baseline, head, merge));
    }

    [Fact]
    public async Task PrGateWithoutWorkflowsSucceedsWithoutRestoringAnyTool()
    {
        using var sample = new Repository();
        await sample.InitializeAsync();
        sample.Write("notes.txt", "base");
        var baseline = await sample.CommitAsync();
        sample.Write("notes.txt", "PR");
        var head = await sample.CommitAsync();
        var result = await sample.GateAsync(baseline, head);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Affected workflows: []", result.Text);
        Assert.DoesNotContain("\"dotnet\"", result.Text);
    }

    [Fact]
    public async Task PrGateFailsClosedForMissingInvalidStaleUnrelatedAndShallowRevisions()
    {
        using var sample = new Repository();
        await sample.InitializeAsync();
        sample.Write("workflow.cs", "// root");
        var baseline = await sample.CommitAsync();
        sample.Write("input.txt", "change");
        var head = await sample.CommitAsync();
        Assert.Equal(["."], await sample.SelectedAsync(baseline, head));
        foreach (var invalid in new[] { "", "$(echo injected)", new string('f', 40) })
            Assert.NotEqual(0, (await sample.GateAsync(invalid, head)).ExitCode);
        var stale = await sample.GateAsync(baseline, head, baseline);
        Assert.NotEqual(0, stale.ExitCode);
        Assert.Contains("Checkout does not match", stale.Text);

        await sample.GitAsync("checkout", "--quiet", "--orphan", "feat/unrelated");
        sample.Write("unrelated.txt", "orphan");
        var unrelated = await sample.CommitAsync();
        await sample.GitAsync("checkout", "--quiet", "main");
        Assert.NotEqual(0, (await sample.GateAsync(baseline, unrelated, head)).ExitCode);
        sample.Write(".git/shallow", baseline + "\n");
        var shallow = await sample.GateAsync(baseline, head);
        Assert.NotEqual(0, shallow.ExitCode);
        Assert.Contains("Full checkout history required", shallow.Text);
    }

    [Fact]
    public void PrGateHasOneStableAlwaysTriggeredLeastPrivilegeCheck()
    {
        var workflow = System.IO.File.ReadAllText(System.IO.Path.Combine(TemplateDirectory,
            ".github", "workflows", "pr-gate.yml"));
        Assert.Contains("name: Workflow PR gate", workflow);
        Assert.Contains("  pull_request:", workflow);
        Assert.Contains("  contents: read", workflow);
        Assert.Contains("fetch-depth: 0", workflow);
        Assert.Contains("persist-credentials: false", workflow);
        Assert.Contains("run: python3 .github/scripts/pr_gate.py", workflow);
        Assert.Contains("PR_BASE_SHA: ${{ github.event.pull_request.base.sha }}", workflow);
        Assert.DoesNotContain("pull_request_target", workflow);
        Assert.DoesNotContain("secrets.", workflow);
        Assert.DoesNotContain("id-token:", workflow);
        Assert.DoesNotContain("continue-on-error", workflow);
        Assert.DoesNotContain("if:", workflow);
        Assert.DoesNotContain("paths:", workflow);
        Assert.DoesNotContain("matrix:", workflow);
        Assert.DoesNotContain("run: ${{", workflow);
        Assert.Equal(1, workflow.Split("    runs-on:").Length - 1);
    }

    [Fact]
    public async Task MergeGateSelectsFullPushRangeAndInitialPushWithoutPublishingDuringValidation()
    {
        using var sample = new Repository();
        await sample.InitializeAsync();
        sample.Write("alpha/workflow.cs", "// a");
        sample.Write("beta/workflow.cs", "// b");
        var baseline = await sample.CommitAsync();
        sample.Write("alpha/input.txt", "first pushed commit");
        await sample.CommitAsync();
        sample.Write("beta/input.txt", "last pushed commit");
        var head = await sample.CommitAsync();
        foreach (var before in new[] { baseline, new string('0', 40) })
        {
            var selected = await sample.MergeGateAsync(before, head, "--list");
            Assert.Equal(0, selected.ExitCode);
            Assert.Equal(["alpha", "beta"], Assert.IsType<string[]>(JsonSerializer.Deserialize<string[]>(selected.Text.Trim())));
        }
        var stale = await sample.MergeGateAsync(baseline, baseline, "--list");
        Assert.NotEqual(0, stale.ExitCode);
        Assert.Contains("Checkout does not match", stale.Text);
        Directory.Delete(sample.File("alpha"), recursive: true);
        Directory.Delete(sample.File("beta"), recursive: true);
        var empty = await sample.CommitAsync();
        var validated = await sample.MergeGateAsync(head, empty, "--validate");
        Assert.Equal(0, validated.ExitCode);
        Assert.Contains("Affected workflows: []", validated.Text);
        Assert.DoesNotContain("\"dotnet\"", validated.Text);
    }

    [Theory]
    [InlineData("pull_request", "refs/heads/main", "test-token", "main-branch push")]
    [InlineData("push", "refs/heads/feature", "test-token", "main-branch push")]
    [InlineData("push", "refs/heads/main", "", "Registrar app token")]
    public async Task MergeGateRejectsUntrustedPublicationContextBeforeInvokingTools(
        string eventName, string reference, string token, string diagnostic)
    {
        using var sample = new Repository();
        await sample.InitializeAsync();
        sample.Write("alpha/workflow.cs", "// a");
        var head = await sample.CommitAsync();
        var result = await sample.MergeGateAsync(new string('0', 40), head, "--publish",
            new() { ["GITHUB_EVENT_NAME"] = eventName, ["GITHUB_REF"] = reference, ["WF_ACCESS_TOKEN"] = token });
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(diagnostic, result.Text);
        Assert.DoesNotContain("\"dotnet\"", result.Text);
        Assert.DoesNotContain("test-token", result.Text);
    }

    [Fact]
    public void MergeGateKeepsPrCredentialFreeAndRequestsFederatedAppIdentityOnlyAfterTests()
    {
        var workflow = System.IO.File.ReadAllText(System.IO.Path.Combine(TemplateDirectory,
            ".github", "workflows", "register-on-merge.yml"));
        Assert.Contains("branches: [main]", workflow);
        Assert.Contains("persist-credentials: false", workflow);
        Assert.Contains("fetch-depth: 0", workflow);
        Assert.Contains("cancel-in-progress: false", workflow);
        Assert.Contains("queue: max", workflow);
        Assert.Contains("id-token: write", workflow);
        Assert.Contains("allow-no-subscriptions: true", workflow);
        Assert.Contains("::add-mask::$token", workflow);
        Assert.Contains("WF_ACCESS_TOKEN=\"$token\" python3", workflow);
        Assert.Equal(2, workflow.Split("if: steps.validated.outputs.has-workflows == 'true'").Length - 1);
        Assert.True(workflow.IndexOf("--validate", StringComparison.Ordinal) <
            workflow.IndexOf("uses: azure/login@", StringComparison.Ordinal));
        Assert.DoesNotContain("pull_request", workflow);
        Assert.DoesNotContain("client-secret", workflow);
        Assert.DoesNotContain("GITHUB_ENV", workflow);
        Assert.DoesNotContain("continue-on-error", workflow);
    }

    [Fact]
    public async Task PrGateRunsRealGeneratedWorkflowAndRejectsInvalidDagFailingTestAndRestore()
    {
        using var sample = new Repository();
        var feed = System.IO.Path.Combine(sample.Root, "f");
        var hive = System.IO.Path.Combine(sample.Root, "h");
        Directory.CreateDirectory(feed);
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        foreach (var name in new[] { "Wf.Contracts", "Wf.Engine.Core", "Wf.Validation", "Wf.Sdk", "Wf.Sdk.Testing" })
            await sample.DotnetAsync(SourceRepository, "pack",
                System.IO.Path.Combine(SourceRepository, "backend", "src", name, name + ".csproj"),
                "--no-build", "--no-restore", "--configuration", configuration, "--output", feed, "--nologo");
        await sample.DotnetAsync(SourceRepository, "pack",
            System.IO.Path.Combine(SourceRepository, "backend", "src", "Wf.Cli", "Wf.Cli.csproj"),
            "--configuration", configuration, "--output", feed, "--nologo");
        await sample.DotnetAsync(SourceRepository, "pack",
            System.IO.Path.Combine(SourceRepository, "backend", "src", "Wf.Templates", "Wf.Templates.csproj"),
            "--configuration", configuration, "--output", feed, "--nologo");
        CopyRestoredDependencies(feed);
        await sample.DotnetAsync(SourceRepository, "new", "install",
            System.IO.Path.Combine(feed, "Wf.Templates.1.0.0.nupkg"), "--debug:custom-hive", hive);
        var templates = await sample.DotnetAsync(SourceRepository, "new", "list",
            "--debug:custom-hive", hive);
        if (!templates.Text.Contains("wf-repo", StringComparison.Ordinal))
            await sample.DotnetAsync(SourceRepository, "new", "install", TemplateDirectory,
                "--debug:custom-hive", hive);
        await sample.DotnetAsync(SourceRepository, "new", "wf-repo", "--output", sample.Path,
            "--debug:custom-hive", hive);
        Assert.False(Directory.Exists(sample.File(".template.config")));
        var toolManifest = sample.File(".config/dotnet-tools.json");
        Assert.Contains("\"version\": \"1.0.0\"", await System.IO.File.ReadAllTextAsync(toolManifest,
            TestContext.Current.CancellationToken));
        var directory = sample.File("workflows/team δ/sample");
        await sample.DotnetAsync(SourceRepository, "new", "wf-workflow", "--name", "GateSample",
            "--output", directory, "--debug:custom-hive", hive);
        sample.Write("NuGet.Config", $"""
            <configuration>
              <packageSources>
                <clear />
                <add key="local" value="{System.Security.SecurityElement.Escape(feed)}" />
              </packageSources>
              <disabledPackageSources><clear /></disabledPackageSources>
              <packageSourceMapping><clear /></packageSourceMapping>
            </configuration>
            """);
        sample.Write("workflows/team δ/sample/input ' & [δ] $(echo nope).txt", "special-character workflow input");
        await sample.InitializeAsync();
        var baseline = await sample.CommitAsync();
        var scriptPath = System.IO.Path.Combine(directory, "workflow.cs");
        var script = await System.IO.File.ReadAllTextAsync(scriptPath, TestContext.Current.CancellationToken);
        await System.IO.File.WriteAllTextAsync(scriptPath, script.Replace("Workflow checks passed.",
            "Valid PR checks passed.", StringComparison.Ordinal), TestContext.Current.CancellationToken);
        var valid = await sample.CommitAsync();
        var passed = await sample.GateAsync(baseline, valid);
        Assert.True(passed.ExitCode == 0, passed.Text);
        Assert.Contains("Valid: GateSample@1.0.0", passed.Text);
        Assert.Matches(@"succeeded:\s+4\b", passed.Text);
        Assert.Contains("\"command\": [\"dotnet\", \"test\"]", passed.Text);
        Assert.DoesNotContain("CLI_PACKAGE_VERSION", await System.IO.File.ReadAllTextAsync(toolManifest,
            TestContext.Current.CancellationToken));
        output.WriteLine("REAL valid generated PR gate: exit 0; wf validate + exact dotnet test, 4 passing tests.");
        var mergeValidated = await sample.MergeGateAsync(baseline, valid, "--validate");
        Assert.True(mergeValidated.ExitCode == 0, mergeValidated.Text);
        Assert.Contains("Valid: GateSample@1.0.0", mergeValidated.Text);
        Assert.Matches(@"succeeded:\s+4\b", mergeValidated.Text);
        output.WriteLine("REAL generated main-push validation: exact shared validator and 4 passing tests, no credentials.");

        await System.IO.File.WriteAllTextAsync(scriptPath, script.Replace(".DependsOn(\"prepare\")",
            ".DependsOn(\"missing\")", StringComparison.Ordinal), TestContext.Current.CancellationToken);
        var invalidDag = await sample.CommitAsync();
        var rejectedDag = await sample.GateAsync(valid, invalidDag);
        Assert.NotEqual(0, rejectedDag.ExitCode);
        Assert.Contains("missing", rejectedDag.Text);
        Assert.DoesNotContain("\"command\": [\"dotnet\", \"test\"]", rejectedDag.Text);
        output.WriteLine("REAL invalid DAG PR gate: nonzero; validation diagnostic names missing dependency.");

        await System.IO.File.WriteAllTextAsync(scriptPath, script, TestContext.Current.CancellationToken);
        sample.Write("workflows/team δ/sample/Tests/DeliberateFailure.cs", """
            public sealed class DeliberateFailure
            {
                [Fact]
                public void FailsThePrGate() => Assert.Fail("PR_GATE_EXPECTED_TEST_FAILURE");
            }
            """);
        var failingTest = await sample.CommitAsync();
        var rejectedTest = await sample.GateAsync(invalidDag, failingTest);
        Assert.NotEqual(0, rejectedTest.ExitCode);
        Assert.Contains("Valid: GateSample@1.0.0", rejectedTest.Text);
        Assert.Contains("PR_GATE_EXPECTED_TEST_FAILURE", rejectedTest.Text);
        Assert.Contains("\"command\": [\"dotnet\", \"test\"]", rejectedTest.Text);
        output.WriteLine("REAL failing-test PR gate: nonzero after successful wf validate; test failure propagated.");

        System.IO.File.Delete(System.IO.Path.Combine(directory, "GateSample.Tests.csproj"));
        var missingProject = await sample.CommitAsync();
        var rejectedProject = await sample.GateAsync(failingTest, missingProject);
        Assert.NotEqual(0, rejectedProject.ExitCode);
        Assert.Contains("\"command\": [\"dotnet\", \"test\"]", rejectedProject.Text);

        var manifest = await System.IO.File.ReadAllTextAsync(toolManifest, TestContext.Current.CancellationToken);
        await System.IO.File.WriteAllTextAsync(toolManifest, manifest.Replace("\"1.0.0\"", "\"99.99.99\"",
            StringComparison.Ordinal), TestContext.Current.CancellationToken);
        var badRestore = await sample.CommitAsync();
        var rejectedRestore = await sample.GateAsync(missingProject, badRestore);
        Assert.NotEqual(0, rejectedRestore.ExitCode);
        Assert.Contains("\"tool\", \"restore\"", rejectedRestore.Text);
        Assert.DoesNotContain("\"run\", \"wf\"", rejectedRestore.Text);
        output.WriteLine("REAL missing-test-project and unavailable-tool PR gates: nonzero, no success fallback.");
    }

    private static void CopyRestoredDependencies(string feed)
    {
        using var assets = JsonDocument.Parse(System.IO.File.ReadAllBytes(System.IO.Path.Combine(SourceRepository,
            "backend", "tests", "Wf.Templates.Tests", "obj", "project.assets.json")));
        var folders = assets.RootElement.GetProperty("packageFolders").EnumerateObject()
            .Select(folder => folder.Name).ToArray();
        foreach (var library in assets.RootElement.GetProperty("libraries").EnumerateObject()
                     .Where(library => library.Value.GetProperty("type").GetString() == "package"))
        {
            var relative = library.Value.GetProperty("path").GetString()!.Replace('/', System.IO.Path.DirectorySeparatorChar);
            var name = library.Name.Replace('/', '.').ToLowerInvariant() + ".nupkg";
            System.IO.File.Copy(folders.Select(folder => System.IO.Path.Combine(folder, relative, name))
                .First(System.IO.File.Exists), System.IO.Path.Combine(feed, name));
        }
    }

    private static string SourceRepository { get; } = FindRepository();
    private static string TemplateDirectory => System.IO.Path.Combine(SourceRepository, "backend", "src",
        "Wf.Templates", "workflows-repo");

    private static string FindRepository([CallerFilePath] string file = "")
    {
        for (var directory = new DirectoryInfo(System.IO.Path.GetDirectoryName(file)!);
             directory is not null; directory = directory.Parent)
            if (System.IO.File.Exists(System.IO.Path.Combine(directory.FullName, "backend", "src",
                    "Wf.Templates", "Wf.Templates.csproj")))
                return directory.FullName;
        throw new DirectoryNotFoundException("PR gate tests require the repository source tree.");
    }

    private sealed class Repository : IDisposable
    {
        public string Root { get; } = System.IO.Path.Combine(SourceRepository, ".wfpg" + Guid.NewGuid().ToString("N")[..8]);
        public string Path => System.IO.Path.Combine(Root, "r");

        public Repository()
        {
            Directory.CreateDirectory(Path);
        }

        public string File(string relative) => System.IO.Path.Combine(Path,
            relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

        public void Write(string relative, string content)
        {
            var file = File(relative);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
            System.IO.File.WriteAllText(file, content);
        }

        public async Task InitializeAsync()
        {
            await GitAsync("init", "--quiet", "--initial-branch=main");
            await GitAsync("config", "user.name", "PR gate fixture");
            await GitAsync("config", "user.email", "pr-gate@example.invalid");
            await GitAsync("config", "commit.gpgsign", "false");
        }

        public async Task<string> CommitAsync()
        {
            await GitAsync("add", "--all");
            await GitAsync("commit", "--quiet", "--no-gpg-sign", "-m", "test: Update local PR gate fixture");
            return (await GitAsync("rev-parse", "HEAD")).Text.Trim();
        }

        public Task<Result> GitAsync(params string[] arguments) => RunAsync(Path, "git", ["--no-pager", .. arguments]);
        public Task<Result> DotnetAsync(string directory, params string[] arguments) =>
            RunAsync(directory, "dotnet", arguments);

        public async Task<string[]> SelectedAsync(string baseline, string head, string? checkout = null)
        {
            var result = await GateAsync(baseline, head, checkout, list: true);
            Assert.True(result.ExitCode == 0, result.Text);
            return JsonSerializer.Deserialize<string[]>(result.Text.Trim())!;
        }

        public Task<Result> GateAsync(string baseline, string head, string? checkout = null, bool list = false)
        {
            var helper = File(".github/scripts/pr_gate.py");
            if (!System.IO.File.Exists(helper))
                helper = System.IO.Path.Combine(TemplateDirectory, ".github", "scripts", "pr_gate.py");
            return RunAsync(Path, OperatingSystem.IsWindows() ? "python" : "python3",
                list ? [helper, "--list"] : [helper], assertSuccess: false,
                new Dictionary<string, string>
                {
                    ["PR_BASE_SHA"] = baseline, ["PR_HEAD_SHA"] = head, ["PR_CHECKOUT_SHA"] = checkout ?? head
                });
        }

        public Task<Result> MergeGateAsync(string before, string head, string mode,
            Dictionary<string, string>? environment = null)
        {
            var values = new Dictionary<string, string>
            {
                ["PUBLISH_BASE_SHA"] = before, ["PUBLISH_HEAD_SHA"] = head,
                ["GITHUB_EVENT_NAME"] = "push", ["GITHUB_REF"] = "refs/heads/main", ["GITHUB_SHA"] = head,
                ["WF_ACCESS_TOKEN"] = "", ["WF_ENGINE_URL"] = "http://127.0.0.1:1"
            };
            foreach (var item in environment ?? []) values[item.Key] = item.Value;
            return RunAsync(Path, OperatingSystem.IsWindows() ? "python" : "python3",
                [System.IO.Path.Combine(TemplateDirectory, ".github", "scripts", "register_on_merge.py"), mode],
                assertSuccess: false, values);
        }

        private async Task<Result> RunAsync(string directory, string executable, string[] arguments,
            bool assertSuccess = true, Dictionary<string, string>? environment = null)
        {
            var start = new ProcessStartInfo(executable)
            {
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
            start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            start.Environment["DOTNET_NOLOGO"] = "1";
            if (environment is not null)
            {
                start.Environment["NUGET_PACKAGES"] = System.IO.Path.Combine(Root, "p");
                start.Environment["DOTNET_CLI_HOME"] = System.IO.Path.Combine(Root, "c");
                foreach (var (name, value) in environment)
                    start.Environment[name] = value;
            }
            foreach (var argument in arguments)
                start.ArgumentList.Add(argument);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(4));
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                await Task.WhenAll(stdout, stderr);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
                throw;
            }
            var result = new Result(process.ExitCode, await stdout + await stderr);
            if (assertSuccess)
                Assert.True(result.ExitCode == 0, $"{executable} {string.Join(' ', arguments)}\n{result.Text}");
            return result;
        }

        public void Dispose()
        {
            if (!Directory.Exists(Root))
                return;
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                System.IO.File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(Root, recursive: true);
        }
    }

    private sealed record Result(int ExitCode, string Text);
}
