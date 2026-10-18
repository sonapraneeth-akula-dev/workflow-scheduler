using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Wf.Contracts.Manifest;
using Wf.Contracts.Registry;
using Wf.ControlPlane.Auth;
using Wf.ControlPlane.Storage;
using Wf.Sdk;

namespace Wf.Integration.Tests.Auth;

public sealed partial class AuthorizationMatrixTests
{
    private static CancellationToken CloneToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Clone_HTTP_historical_deleted_source_real_offline_CLI_validate_retains_tasks_lineage_and_all_database_bytes()
    {
        var group = Guid.NewGuid();
        var seed = await Seed(RegistryRole.Viewer, group, false);
        var targetType = seed.Team + "/sibling";
        await Bind(targetType, RegistryRole.Author, group, RolePrincipalKind.Group, RoleScopeKind.Type);
        using var storage = new CloneStorage();
        var workflow = seed.Type + "/original";
        var selected = await StoreCloneVersion(storage.Inner, workflow, "2.3.4", 42);
        var active = await StoreCloneVersion(storage.Inner, workflow, "3.0.0", 99);
        await CloneExecute("UPDATE workflows SET deleted_at=now() WHERE id=$1", workflow);
        var before = await CloneSnapshot(workflow);
        await using var server = await Server.Start(database.DataSource,
            configureServices: services => services.AddSingleton<IBlobStore>(storage));
        using var response = await ClonePost(server, workflow, selected, server.Token(group: group),
            new { newName = "experiment", targetType });
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CloneToken));
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("workflow-clone.zip", response.Content.Headers.ContentDisposition!.ToString());
        var generated = Path.Combine(storage.Root, "download");
        using (var zip = new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync(CloneToken))))
            zip.ExtractToDirectory(generated);
        var script = await File.ReadAllTextAsync(Path.Combine(generated, "workflow.cs"), CloneToken);
        Assert.Contains("ctx.Outputs[\"answer\"] = 42", script);
        Assert.DoesNotContain("= 99", script);
        Assert.Contains("ClonedFrom", script);
        Assert.Contains(workflow + "@" + selected, script);
        var feed = await CloneOfflineFeed(storage.Root);
        var packages = Path.Combine(storage.Root, "packages");
        var result = await CloneDotnet(generated, feed, packages, typeof(Wf.Cli.Program).Assembly.Location,
            "validate", generated, "--work-directory", Path.Combine(storage.Root, "build"));
        Assert.True(result.Exit == 0, result.Error + result.Output);
        Assert.Contains("Valid: " + targetType + "/experiment@1.0.0", result.Output);
        var manifests = Directory.GetFiles(Path.Combine(storage.Root, "build", "c"), "manifest.json", SearchOption.AllDirectories);
        var manifest = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(Assert.Single(manifests), CloneToken),
            WorkflowManifestJsonContext.Default.WorkflowManifest)!;
        Assert.Equal(targetType + "/experiment", manifest.Name);
        Assert.Equal("sibling", manifest.Metadata.Type);
        Assert.Equal(workflow + "@" + selected, manifest.Metadata.Values["ClonedFrom"]);
        Assert.Equal(["work"], manifest.Nodes.Select(node => node.Key));
        Assert.Equal(["echo"], manifest.Nodes[0].Tasks.Select(task => task.Key));
        var tests = await CloneDotnet(generated, feed, packages, "test", "WorkflowClone.Tests.csproj", "--nologo");
        Assert.True(tests.Exit == 0, tests.Error + tests.Output);
        Assert.Matches(@"succeeded:\s+1\b", tests.Output + tests.Error);
        Assert.Equal(before, await CloneSnapshot(workflow));
        Assert.Equal(active, await CloneValue("SELECT active_version_id FROM workflows WHERE id=$1", workflow));
        Assert.Equal(0L, await CloneValue("SELECT count(*) FROM workflows WHERE id=$1", targetType + "/experiment"));
        Assert.Equal(0L, await CloneValue("SELECT count(*) FROM workflow_versions WHERE workflow_id=$1", targetType + "/experiment"));
        Assert.Equal(0L, await CloneValue("SELECT count(*) FROM schedules WHERE workflow_id=$1", workflow));
        Assert.Equal(0L, await CloneValue("SELECT count(*) FROM audit_log WHERE target_id=$1", workflow));
        Assert.Equal(1, storage.Reads);
    }

    [Theory]
    [InlineData(RegistryRole.Viewer, RegistryRole.Viewer, false)]
    [InlineData(RegistryRole.Viewer, RegistryRole.Operator, false)]
    [InlineData(RegistryRole.Operator, RegistryRole.Author, true)]
    [InlineData(RegistryRole.Viewer, RegistryRole.Author, true)]
    [InlineData(RegistryRole.Author, RegistryRole.Admin, true)]
    [InlineData(RegistryRole.Admin, RegistryRole.Author, true)]
    public async Task Clone_HTTP_requires_fresh_independent_source_read_and_exact_target_author_scope(
        RegistryRole sourceRole, RegistryRole targetRole, bool allowed)
    {
        var group = Guid.NewGuid();
        var seed = await Seed(sourceRole, group, false);
        await Bind(seed.OtherType, targetRole, group, RolePrincipalKind.Group, RoleScopeKind.Type);
        using var storage = new CloneStorage();
        var workflow = seed.Type + "/clone-role";
        var id = await StoreCloneVersion(storage.Inner, workflow, "1.0.0");
        await using var server = await Server.Start(database.DataSource,
            configureServices: services => services.AddSingleton<IBlobStore>(storage));
        using var response = await ClonePost(server, workflow, id, server.Token(group: group),
            new { newName = "copy", targetType = seed.OtherType });
        Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(allowed ? 1 : 0, storage.Reads);
        if (!allowed) return;
        await CloneExecute("DELETE FROM role_bindings WHERE scope_id=$1 AND principal_id=$2", seed.OtherType, group);
        using var targetRevoked = await ClonePost(server, workflow, id, server.Token(group: group),
            new { newName = "copy", targetType = seed.OtherType });
        Assert.Equal(HttpStatusCode.Forbidden, targetRevoked.StatusCode);
        await Bind(seed.OtherType, RegistryRole.Author, group, RolePrincipalKind.Group, RoleScopeKind.Type);
        await CloneExecute("DELETE FROM role_bindings WHERE scope_id=$1 AND principal_id=$2", seed.Type, group);
        using var sourceRevoked = await ClonePost(server, workflow, id, server.Token(group: group),
            new { newName = "copy", targetType = seed.OtherType });
        Assert.Equal(HttpStatusCode.Forbidden, sourceRevoked.StatusCode);
        Assert.Equal(1, storage.Reads);
    }

    [Fact]
    public async Task Clone_HTTP_target_Author_does_not_grant_foreign_source_read_or_bypass_exact_version_workflow_ownership()
    {
        var group = Guid.NewGuid();
        var seed = await Seed(RegistryRole.Author, group, false);
        using var storage = new CloneStorage();
        var workflow = seed.Type + "/owned";
        var id = await StoreCloneVersion(storage.Inner, workflow, "1.0.0");
        var other = await StoreCloneVersion(storage.Inner, seed.Type + "/other", "1.0.0");
        var foreign = await StoreCloneVersion(storage.Inner, seed.OtherType + "/foreign", "1.0.0");
        await using var server = await Server.Start(database.DataSource,
            configureServices: services => services.AddSingleton<IBlobStore>(storage));
        foreach (var version in new[] { other, foreign, Guid.CreateVersion7() })
        {
            using var missing = await ClonePost(server, workflow, version, server.Token(group: group),
                new { newName = "copy", targetType = seed.Type });
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
        using (var denied = await ClonePost(server, seed.OtherType + "/foreign", foreign, server.Token(group: group),
            new { newName = "copy", targetType = seed.Type }))
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var registrar = await ClonePost(server, workflow, id,
            server.Token(group: group, identity: "app", scope: null, role: "Registrar"),
            new { newName = "copy", targetType = seed.Type }))
            Assert.Equal(HttpStatusCode.Forbidden, registrar.StatusCode);
        using var admin = await ClonePost(server, workflow, id, server.Token(identity: "app", scope: null, role: "Admin"),
            new { newName = "copy", targetType = seed.Type });
        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
        Assert.Equal(1, storage.Reads);
    }

    [Fact]
    public async Task Clone_HTTP_contract_errors_conflicts_anonymous_and_noncanonical_routes_never_read_source()
    {
        var group = Guid.NewGuid();
        var seed = await Seed(RegistryRole.Author, group, false);
        using var storage = new CloneStorage();
        var workflow = seed.Type + "/contract";
        var id = await StoreCloneVersion(storage.Inner, workflow, "1.0.0");
        await StoreCloneVersion(storage.Inner, seed.Type + "/existing", "1.0.0");
        await CloneExecute("UPDATE workflows SET deleted_at=now() WHERE id=$1", seed.Type + "/existing");
        await using var server = await Server.Start(database.DataSource,
            configureServices: services => services.AddSingleton<IBlobStore>(storage));
        var jwt = server.Token(group: group);
        foreach (var body in new[]
        {
            "{}", "null", "[]", "{", "{\"newName\":\"copy\"}", "{\"newName\":null,\"targetType\":\"" + seed.Type + "\"}",
            "{\"newName\":\"copy\",\"newName\":\"other\",\"targetType\":\"" + seed.Type + "\"}",
            "{\"newName\":\"copy\",\"targetType\":\"" + seed.Type + "\",\"unexpected\":1}",
            "{\"NewName\":\"copy\",\"targetType\":\"" + seed.Type + "\"}",
            "{\"newName\":1,\"targetType\":\"" + seed.Type + "\"}",
            "{\"newName\":\"../escape\",\"targetType\":\"" + seed.Type + "\"}",
            "{\"newName\":\"copy\",\"targetType\":\"release\"}",
            "{\"newName\":\"copy\",\"targetType\":\"" + seed.Type + "/foreign\"}",
            "{\"newName\":\"contract\",\"targetType\":\"" + seed.Type + "\"}",
            new string(' ', 4097)
        })
        {
            using var invalid = await ClonePostRaw(server, ClonePath(workflow, id), jwt, body);
            await CloneProblem(invalid, HttpStatusCode.BadRequest, "invalid_clone_request");
        }
        foreach (var version in new[] { "1.0.0", Guid.Empty.ToString(), id.ToString("N"), id.ToString().ToUpperInvariant() })
        {
            using var invalid = await ClonePostRaw(server, ClonePath(workflow, id).Replace(id.ToString(), version),
                jwt, "{\"newName\":\"copy\",\"targetType\":\"" + seed.Type + "\"}");
            await CloneProblem(invalid, HttpStatusCode.BadRequest, "invalid_version_identity");
        }
        using (var query = await ClonePostRaw(server, ClonePath(workflow, id) + "?targetType=foreign",
            jwt, "{\"newName\":\"copy\",\"targetType\":\"" + seed.Type + "\"}"))
            await CloneProblem(query, HttpStatusCode.BadRequest, "invalid_version_identity");
        using (var anonymous = await ClonePost(server, workflow, id, null, new { newName = "copy", targetType = seed.Type }))
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using (var existing = await ClonePost(server, workflow, id, jwt, new { newName = "existing", targetType = seed.Type }))
            await CloneProblem(existing, HttpStatusCode.Conflict, "clone_target_exists");
        using (var media = await ClonePostRaw(server, ClonePath(workflow, id), jwt, "{}", "text/plain"))
            await CloneProblem(media, HttpStatusCode.UnsupportedMediaType, "invalid_clone_content_type");
        Assert.Equal(0, storage.Reads);
    }

    [Theory]
    [InlineData("hash", HttpStatusCode.InternalServerError, "version_data_inconsistent")]
    [InlineData("size", HttpStatusCode.InternalServerError, "version_data_inconsistent")]
    [InlineData("manifest", HttpStatusCode.InternalServerError, "version_data_inconsistent")]
    [InlineData("type", HttpStatusCode.InternalServerError, "version_data_inconsistent")]
    [InlineData("zip", HttpStatusCode.InternalServerError, "version_data_inconsistent")]
    [InlineData("layout", HttpStatusCode.InternalServerError, "version_data_inconsistent")]
    [InlineData("utf8", HttpStatusCode.InternalServerError, "version_data_inconsistent")]
    [InlineData("missing", HttpStatusCode.ServiceUnavailable, "version_source_unavailable")]
    [InlineData("unsupported", HttpStatusCode.BadRequest, "unsupported_clone_source")]
    public async Task Clone_HTTP_corruption_unavailable_and_unsupported_source_return_sanitized_problem_not_partial_zip(
        string failure, HttpStatusCode status, string code)
    {
        var group = Guid.NewGuid();
        var seed = await Seed(RegistryRole.Author, group, false);
        using var storage = new CloneStorage();
        var workflow = seed.Type + "/broken";
        var id = await StoreCloneVersion(storage.Inner, workflow, "1.0.0");
        var sql = failure switch
        {
            "hash" => "UPDATE workflow_versions SET source_sha256=decode(repeat('e',64),'hex'),source_uri='sources/'||repeat('e',64) WHERE id=$1",
            "size" => "UPDATE workflow_versions SET source_size=source_size+1 WHERE id=$1",
            "manifest" => "UPDATE workflow_versions SET manifest=jsonb_set(manifest,'{nodes}','null') WHERE id=$1",
            "type" => "UPDATE workflow_versions SET manifest=jsonb_set(manifest,'{metadata,type}','\"wrong\"') WHERE id=$1",
            _ => null
        };
        if (sql is not null) await CloneExecute(sql, id);
        if (failure == "hash")
            await File.WriteAllBytesAsync(Path.Combine(storage.Root, "blobs", "sources", new string('e', 64)),
                DiffArchive(Encoding.UTF8.GetBytes("private-source")), CloneToken);
        if (failure == "missing")
            foreach (var file in Directory.GetFiles(Path.Combine(storage.Root, "blobs", "sources"))) File.Delete(file);
        if (failure is "zip" or "utf8" or "layout" or "unsupported")
        {
            var bytes = failure switch
            {
                "zip" => new byte[] { 1, 2, 3 },
                "utf8" => DiffArchive([0xff, 0xfe]),
                "unsupported" => DiffArchive(Encoding.UTF8.GetBytes("// private-source\nvar workflow = Configure();")),
                _ => CloneArchive("nested/workflow.cs", "private-source")
            };
            await SetDiffSource(storage.Inner, id, bytes);
        }
        await using var server = await Server.Start(database.DataSource,
            configureServices: services => services.AddSingleton<IBlobStore>(storage));
        using var response = await ClonePost(server, workflow, id, server.Token(group: group),
            new { newName = "copy", targetType = seed.Type });
        await CloneProblem(response, status, code);
        var body = await response.Content.ReadAsStringAsync(CloneToken);
        foreach (var sensitive in new[] { "private-source", storage.Root, "sources/", "stackTrace", "Workflow.Define" })
            Assert.DoesNotContain(sensitive, body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Clone_HTTP_database_outage_is_503_and_openapi_documents_JSON_request_and_ZIP()
    {
        using var storage = new CloneStorage();
        await using (var unavailable = Npgsql.NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Username=unused;Database=unused;Timeout=1"))
        await using (var server = await Server.Start(unavailable,
            configureServices: services => services.AddSingleton<IBlobStore>(storage)))
        {
            using var response = await ClonePost(server, "team/type/workflow", Guid.CreateVersion7(), server.Token(role: "Admin"),
                new { newName = "copy", targetType = "team/type" });
            await CloneProblem(response, HttpStatusCode.ServiceUnavailable, "version_data_unavailable");
        }
        await using var healthy = await Server.Start(database.DataSource,
            configureServices: services => services.AddSingleton<IBlobStore>(storage));
        using var openapi = await healthy.GetPath("/openapi/v1.json", healthy.Token(role: "Admin"));
        using var document = JsonDocument.Parse(await openapi.Content.ReadAsByteArrayAsync(CloneToken));
        var operation = document.RootElement.GetProperty("paths")
            .GetProperty("/api/v1/workflows/{team}/{type}/{workflow}/versions/{version}/clone").GetProperty("post");
        Assert.True(operation.GetProperty("requestBody").GetProperty("content").TryGetProperty("application/json", out _));
        Assert.True(operation.GetProperty("responses").GetProperty("200").GetProperty("content").TryGetProperty("application/zip", out _));
        foreach (var status in new[] { "400", "401", "403", "404", "409", "415", "500", "503" })
            Assert.True(operation.GetProperty("responses").TryGetProperty(status, out _));
        Assert.Equal(0, storage.Reads);
    }

    [Fact]
    public async Task Clone_HTTP_storage_outage_is_explicit_and_request_cancellation_reaches_storage_without_mutation()
    {
        var group = Guid.NewGuid();
        var seed = await Seed(RegistryRole.Author, group, false);
        using var storage = new CloneStorage { FailDownload = true };
        var workflow = seed.Type + "/cancel";
        var id = await StoreCloneVersion(storage.Inner, workflow, "1.0.0");
        var before = await CloneSnapshot(workflow);
        await using var server = await Server.Start(database.DataSource,
            configureServices: services => services.AddSingleton<IBlobStore>(storage));
        using (var unavailable = await ClonePost(server, workflow, id, server.Token(group: group),
            new { newName = "copy", targetType = seed.Type }))
            await CloneProblem(unavailable, HttpStatusCode.ServiceUnavailable, "version_source_unavailable");
        storage.FailDownload = false;
        storage.WaitForCancellation = true;
        using var client = new HttpClient { BaseAddress = new(server.Api) };
        using var request = new HttpRequestMessage(HttpMethod.Post, ClonePath(workflow, id))
        {
            Content = JsonContent.Create(new { newName = "copy", targetType = seed.Type })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", server.Token(group: group));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(CloneToken);
        var pending = client.SendAsync(request, cancel.Token);
        await storage.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), CloneToken);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await storage.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10), CloneToken);
        Assert.Equal(before, await CloneSnapshot(workflow));
        Assert.Equal(0L, await CloneValue("SELECT count(*) FROM workflows WHERE id=$1", seed.Type + "/copy"));
    }

    private async Task<Guid> StoreCloneVersion(IBlobStore store, string workflow, string semver, int answer = 42)
    {
        var id = await StorePlanVersion(workflow, semver);
        var definition = Workflow.Define(workflow, semver, w => w.Type(workflow.Split('/')[1])
            .Node("work", n => n.Task("echo", ctx => ctx.Outputs["answer"] = answer)));
        await CloneExecute("UPDATE workflow_versions SET manifest=$2::jsonb WHERE id=$1", id, ManifestWriter.Write(definition));
        var script = $$"""
            #:package Wf.Sdk@1.0.0
            #:property PublishAot=false
            #:property AssemblyName=Clone.Integration.Workflow
            using Wf.Sdk;
            var workflow = Workflow.Define("{{workflow}}", "{{semver}}", w => w.Type("{{workflow.Split('/')[1]}}")
                .Node("work", n => n.Task("echo", ctx => ctx.Outputs["answer"] = {{answer}})));
            return await workflow.RunAsync(args);
            """;
        await SetDiffSource(store, id, DiffArchive(Encoding.UTF8.GetBytes(script)));
        return id;
    }

    private async Task<string> CloneSnapshot(string workflow) =>
        (string)(await CloneValue("""
            SELECT jsonb_build_object('workflow',to_jsonb(w),'versions',
                (SELECT jsonb_agg(to_jsonb(v) ORDER BY id) FROM workflow_versions v WHERE workflow_id=w.id))::text
            FROM workflows w WHERE id=$1
            """, workflow))!;

    private async Task<object?> CloneValue(string sql, params object[] parameters)
    {
        await using var query = database.DataSource.CreateCommand(sql);
        foreach (var value in parameters) query.Parameters.AddWithValue(value);
        return await query.ExecuteScalarAsync(CloneToken);
    }

    private async Task CloneExecute(string sql, params object[] parameters)
    {
        await using var query = database.DataSource.CreateCommand(sql);
        foreach (var value in parameters) query.Parameters.AddWithValue(value);
        await query.ExecuteNonQueryAsync(CloneToken);
    }

    private static string ClonePath(string workflow, Guid version) => VersionPath(workflow, version) + "/clone";
    private static Task<HttpResponseMessage> ClonePost(Server server, string workflow, Guid version, string? jwt, object request) =>
        ClonePostRaw(server, ClonePath(workflow, version), jwt, JsonSerializer.Serialize(request));

    private static async Task<HttpResponseMessage> ClonePostRaw(Server server, string path, string? jwt, string body,
        string mediaType = "application/json")
    {
        using var client = new HttpClient { BaseAddress = new(server.Api) };
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, mediaType) };
        if (jwt is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        return await client.SendAsync(request, CloneToken);
    }

    private static async Task CloneProblem(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(CloneToken));
        Assert.Equal(code, problem.RootElement.GetProperty("code").GetString());
    }

    private static byte[] CloneArchive(string name, string text)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        using (var writer = new StreamWriter(zip.CreateEntry(name).Open()))
            writer.Write(text);
        return output.ToArray();
    }

    private static async Task<string> CloneOfflineFeed(string root)
    {
        var feed = Path.Combine(root, "feed");
        Directory.CreateDirectory(feed);
        var artifacts = Path.Combine(root, "pack-build");
        var packages = Path.Combine(root, "pack-packages");
        var testing = Path.Combine(CloneRepository, "backend", "src", "Wf.Sdk.Testing", "Wf.Sdk.Testing.csproj");
        var attempt = await CloneDotnet(root, feed, packages, "pack", testing, "--no-restore", "--artifacts-path", artifacts,
            "--configuration", "Debug", "--output", feed, "--verbosity", "quiet");
        if (attempt.Exit != 0)
        {
            Assert.Contains("NETSDK1004", attempt.Output + attempt.Error);
            var restore = await CloneDotnet(root, feed, packages, "restore", testing, "--artifacts-path", artifacts, "--verbosity", "quiet");
            Assert.True(restore.Exit == 0, restore.Error + restore.Output);
            attempt = await CloneDotnet(root, feed, packages, "pack", testing, "--no-restore", "--artifacts-path", artifacts,
                "--configuration", "Debug", "--output", feed, "--verbosity", "quiet");
        }
        Assert.True(attempt.Exit == 0, attempt.Error + attempt.Output);
        foreach (var name in new[] { "Wf.Contracts", "Wf.Engine.Core", "Wf.Validation", "Wf.Sdk" })
        {
            var packed = await CloneDotnet(root, feed, packages, "pack",
                Path.Combine(CloneRepository, "backend", "src", name, name + ".csproj"),
                "--no-restore", "--no-build", "--artifacts-path", artifacts,
                "--configuration", "Debug", "--output", feed, "--verbosity", "quiet");
            Assert.True(packed.Exit == 0, packed.Error + packed.Output);
        }
        return feed;
    }

    private static async Task<(int Exit, string Output, string Error)> CloneDotnet(string directory, string feed, string packages,
        params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = directory, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        start.Environment["WarningsNotAsErrors"] = "NU1902;NU1903";
        start.Environment["SourceRevisionId"] = new string('a', 40);
        // The restored global caches are hierarchical local feeds, used read-only. No HTTP sources.
        start.Environment["RestoreSources"] = feed + ";" + string.Join(';', ClonePackageFolders());
        start.Environment["NUGET_PACKAGES"] = packages;
        start.Environment["NuGetAudit"] = "false";
        using var process = Process.Start(start)!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(CloneToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(4));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        return (process.ExitCode, await output, await error);
    }

    private static string[] ClonePackageFolders()
    {
        var artifactRoot = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Parent!.Parent!.FullName;
        var assets = Path.Combine(artifactRoot, "obj", "Wf.Integration.Tests", "project.assets.json");
        if (!File.Exists(assets))
            assets = Path.Combine(CloneRepository, "backend", "tests", "Wf.Integration.Tests", "obj", "project.assets.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(assets));
        return document.RootElement.GetProperty("packageFolders").EnumerateObject().Select(folder =>
            new Uri(Path.GetFullPath(folder.Name)).IsFile ? folder.Name : throw new InvalidDataException("Offline package source must be local."))
            .ToArray();
    }

    private static string CloneRepository => FindCloneRepository();
    private static string FindCloneRepository([CallerFilePath] string file = "")
    {
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(file)!); directory is not null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, "backend", "proto"))) return directory.FullName;
        throw new DirectoryNotFoundException("Clone integration requires repository sources.");
    }

    private sealed class CloneStorage : IBlobStore, IDisposable
    {
        public string Root { get; } = Path.Combine(CloneRepository, ".agent-artifacts", "clone-api-3adec91f", "i", Guid.NewGuid().ToString("N")[..8]);
        public FileSystemBlobStore Inner { get; }
        public int Reads { get; private set; }
        public bool FailDownload { get; set; }
        public bool WaitForCancellation { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CloneStorage() => Inner = new(Path.Combine(Root, "blobs"), RandomNumberGenerator.GetBytes(32));
        public Task UploadAsync(BlobContent content, Stream source, CancellationToken cancellationToken = default) =>
            Inner.UploadAsync(content, source, cancellationToken);
        public async Task DownloadAsync(BlobContent content, Stream destination, CancellationToken cancellationToken = default)
        {
            Reads++;
            if (FailDownload) throw new IOException("private storage endpoint");
            if (WaitForCancellation)
            {
                Started.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
            }
            await Inner.DownloadAsync(content, destination, cancellationToken);
        }
        public void Dispose()
        {
            Inner.Dispose();
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
