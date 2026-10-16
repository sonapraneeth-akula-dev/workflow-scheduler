using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Wf.Contracts.Registry;
using Wf.ControlPlane.Auth;
using Wf.ControlPlane.Storage;

namespace Wf.Integration.Tests.Auth;

public sealed partial class AuthorizationMatrixTests
{
    [Theory]
    [InlineData("historical", 0)]
    [InlineData("denied", 1)]
    [InlineData("missing", 1)]
    [InlineData("conflict", 1)]
    [InlineData("unsupported", 1)]
    public async Task CloneClient_actual_process_uses_signed_HTTP_Postgres_blob_and_never_changes_source(string scenario, int expected)
    {
        var group = Guid.NewGuid();
        var seed = await Seed(RegistryRole.Viewer, group, false);
        if (scenario != "denied")
            await Bind(seed.OtherType, RegistryRole.Author, group, RolePrincipalKind.Group, RoleScopeKind.Type);
        var workflow = seed.Type + "/clone-cli";
        var root = Path.Combine(FindCloneRepository(), ".agent-artifacts", "clone-clients-3adec91f", "i", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        using var store = new FileSystemBlobStore(Path.Combine(root, "blobs"), RandomNumberGenerator.GetBytes(32));
        try
        {
            var oldSource = $$"""
                #:package Wf.Sdk@1.0.0
                using Wf.Sdk;
                System.IO.File.WriteAllText("SHOULD-NOT-EXECUTE", "never");
                var workflow = Workflow.Define("{{workflow}}", "1.0.0", w => w.Type("release")
                    .Node("work", n => n.Task("echo", ctx => ctx.Outputs["value"] = "historical")));
                return await workflow.RunAsync(args);
                """;
            var selected = await StoreDiffVersion(store, workflow, "1.0.0", scenario == "unsupported" ? "// unsupported" : oldSource);
            var latest = await StoreDiffVersion(store, workflow, "2.0.0",
                oldSource.Replace("\"1.0.0\"", "\"2.0.0\"", StringComparison.Ordinal).Replace("\"historical\"", "\"latest\"", StringComparison.Ordinal));
            await using (var activate = database.DataSource.CreateCommand("UPDATE workflows SET active_version_id=$2 WHERE id=$1"))
            {
                activate.Parameters.AddWithValue(workflow);
                activate.Parameters.AddWithValue(latest);
                await activate.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
            if (scenario == "conflict") await StorePlanVersion(seed.OtherType + "/experiment", "1.0.0");
            var before = await CloneClientSnapshot(workflow);
            await using var server = await Server.Start(database.DataSource,
                configureServices: services => services.AddSingleton<IBlobStore>(store));
            var repo = Path.Combine(root, "repo");
            Directory.CreateDirectory(repo);
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var argument in new[] { typeof(Wf.Cli.Program).Assembly.Location, "clone", workflow, "--version",
                (scenario == "missing" ? Guid.CreateVersion7() : selected).ToString("D"), "--new-name", "experiment",
                "--target-type", seed.OtherType, "--repo", repo, "--engine", server.Api })
                start.ArgumentList.Add(argument);
            start.Environment["WF_ACCESS_TOKEN"] = server.Token(group: group);
            start.Environment["SourceRevisionId"] = new string('a', 40);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(40));
            try { await process.WaitForExitAsync(timeout.Token); }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }
            }
            Assert.True(process.ExitCode == expected, await stdout + "\n" + await stderr);
            var destination = Path.Combine(repo, "teams", seed.Other, "release", "experiment");
            if (expected == 0)
            {
                var script = await File.ReadAllTextAsync(Path.Combine(destination, "workflow.cs"), TestContext.Current.CancellationToken);
                Assert.Contains(seed.OtherType + "/experiment", script);
                Assert.Contains(workflow + "@" + selected.ToString("D"), script);
                Assert.Contains("\"historical\"", script);
                Assert.DoesNotContain("\"latest\"", script);
                Assert.Equal(6, Directory.GetFiles(destination, "*", SearchOption.AllDirectories).Length);
                Assert.Contains("Source not executed", await stdout);
            }
            else
            {
                Assert.False(Path.Exists(destination));
                Assert.Contains(scenario switch { "denied" => "403", "missing" => "404", "conflict" => "409", _ => "400" }, await stderr);
            }
            Assert.False(File.Exists(Path.Combine(root, "SHOULD-NOT-EXECUTE")));
            Assert.Equal(before, await CloneClientSnapshot(workflow));
            Assert.Empty(Directory.GetDirectories(repo, ".wf-clone-*", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private async Task<string> CloneClientSnapshot(string workflow)
    {
        await using var query = database.DataSource.CreateCommand("""
            SELECT jsonb_build_object('workflow',(SELECT to_jsonb(w) FROM workflows w WHERE id=$1),
              'versions',(SELECT jsonb_agg(to_jsonb(v) ORDER BY id) FROM workflow_versions v WHERE workflow_id=$1))::text
            """);
        query.Parameters.AddWithValue(workflow);
        return (string)(await query.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static string FindCloneRepository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, "backend", "proto"))) return directory.FullName;
        throw new DirectoryNotFoundException("Clone integration requires repository checkout.");
    }
}
