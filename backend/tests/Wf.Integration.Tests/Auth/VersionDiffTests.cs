using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NpgsqlTypes;
using Wf.Contracts.Definition;
using Wf.Contracts.Manifest;
using Wf.Contracts.Registry;
using Wf.ControlPlane.Api;
using Wf.ControlPlane.Storage;
using Wf.ControlPlane.Versions;

namespace Wf.Integration.Tests.Auth;

public sealed partial class AuthorizationMatrixTests
{
    [Theory]
    [InlineData(RegistryRole.Viewer, false)]
    [InlineData(RegistryRole.Operator, false)]
    [InlineData(RegistryRole.Author, false)]
    [InlineData(RegistryRole.Admin, false)]
    [InlineData(RegistryRole.Viewer, true)]
    [InlineData(RegistryRole.Operator, true)]
    [InlineData(RegistryRole.Author, true)]
    [InlineData(RegistryRole.Admin, true)]
    public async Task ManifestDiff_HTTP_returns_exact_immutable_manifest_and_verified_source_for_each_viewer_grant(
        RegistryRole role, bool teamScope)
    {
        var group = Guid.NewGuid();
        var seed = await Seed(role, group, teamScope);
        var workflow = seed.Type + "/diff";
        using var storage = new DiffStorage();
        const string oldText = "// old <script>\r\nConsole.WriteLine(\"\u03bb\");\r\n";
        const string newText = "// new\nConsole.WriteLine(\"new\");\n";
        var before = await StoreDiffVersion(storage.Store, workflow, "1.0.0", oldText);
        var after = await StoreDiffVersion(storage.Store, workflow, "2.0.0", newText, changed: true);
        await using (var rollback = database.DataSource.CreateCommand("UPDATE workflows SET active_version_id=$2,deleted_at=now() WHERE id=$1"))
        {
            rollback.Parameters.AddWithValue(workflow);
            rollback.Parameters.AddWithValue(before);
            await rollback.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        await using var server = await Server.Start(database.DataSource,
            configureServices: services => services.AddSingleton<IBlobStore>(storage.Store));
        var jwt = server.Token(group: group);
        var diff = await ReadDiff(server, workflow, before, after, jwt);
        Assert.Equal(workflow, diff.WorkflowId);
        Assert.Equal(before, diff.FromVersionId);
        Assert.Equal(after, diff.ToVersionId);
        Assert.Equal("1.0.0", diff.FromVersion);
        Assert.Equal("2.0.0", diff.ToVersion);
        Assert.Equal("workflow.cs", diff.Source.Path);
        Assert.Equal(oldText, diff.Source.Before);
        Assert.Equal(newText, diff.Source.After);
        Assert.True(diff.Source.Changed);
        Assert.Equal(["added", "removed", "work"], diff.Manifest.Nodes.Select(change => change.Key));
        Assert.Equal([ManifestChangeKind.Added, ManifestChangeKind.Removed, ManifestChangeKind.Changed],
            diff.Manifest.Nodes.Select(change => change.Kind));
        Assert.Equal(["added", "changed", "removed"], diff.Manifest.Variables.Select(change => change.Key));
        Assert.Equal(["runTimeout"], diff.Manifest.Policies.Select(change => change.Key));
        var reverse = await ReadDiff(server, workflow, after, before, jwt);
        Assert.Equal(oldText, reverse.Source.After);
        Assert.Equal(newText, reverse.Source.Before);
        Assert.Equal(ManifestChangeKind.Removed, reverse.Manifest.Nodes[0].Kind);
        var same = await ReadDiff(server, workflow, before, before, jwt);
        Assert.False(same.Source.Changed);
        Assert.Empty(same.Manifest.Nodes);
        Assert.Empty(same.Manifest.Variables);
        Assert.Empty(same.Manifest.Policies);
        using var foreign = await server.GetPath(DiffPath(seed.OtherType + "/diff", before, after), jwt);
        await Check(foreign, false);
    }

    [Fact]
    public async Task ManifestDiff_HTTP_checks_exact_pair_ownership_and_rechecks_registry_revocation_before_source_reads()
    {
        var group = Guid.NewGuid();
        var seed = await Seed(RegistryRole.Viewer, group, false);
        using var storage = new DiffStorage();
        var workflow = seed.Type + "/diff-owned";
        var before = await StoreDiffVersion(storage.Store, workflow, "1.0.0", "before");
        var after = await StoreDiffVersion(storage.Store, workflow, "1.0.1", "after");
        var foreign = await StoreDiffVersion(storage.Store, seed.Type + "/different", "1.0.0", "foreign");
        await using var server = await Server.Start(database.DataSource,
            configureServices: services => services.AddSingleton<IBlobStore>(storage.Store));
        var jwt = server.Token(group: group);
        foreach (var (from, to) in new[] { (before, foreign), (foreign, before), (before, Guid.CreateVersion7()) })
        {
            using var missing = await server.GetPath(DiffPath(workflow, from, to), jwt);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
        using (var missingWorkflow = await server.GetPath(DiffPath(workflow + "-missing", before, after), jwt))
            Assert.Equal(HttpStatusCode.NotFound, missingWorkflow.StatusCode);
        await Save(new(seed.Team, [new(seed.Team, "release", "description", new([]))]));
        using (var revoked = await server.GetPath(DiffPath(workflow, before, after), jwt)) await Check(revoked, false);
        using var registrar = await server.GetPath(DiffPath(workflow, before, after),
            server.Token(group: group, identity: "app", scope: null, role: "Registrar"));
        await Check(registrar, false);
        var admin = await ReadDiff(server, workflow, before, after, server.Token(identity: "app", scope: null, role: "Admin"));
        Assert.True(admin.Source.Changed);
    }

    [Fact]
    public async Task ManifestDiff_HTTP_rejects_bad_queries_and_anonymous_requests_without_active_version_fallback()
    {
        var group = Guid.NewGuid();
        var seed = await Seed(RegistryRole.Viewer, group, false);
        using var storage = new DiffStorage();
        var workflow = seed.Type + "/diff-query";
        var id = await StoreDiffVersion(storage.Store, workflow, "1.0.0", "unchanged");
        await using var server = await Server.Start(database.DataSource,
            configureServices: services => services.AddSingleton<IBlobStore>(storage.Store));
        var path = DiffPath(workflow, id, id);
        foreach (var invalid in new[]
        {
            path.Split('?')[0], path + "&from=" + id, path + "&unknown=1", path.Replace("?from=", "?From="),
            path.Replace(id.ToString(), "not-a-version"), path.Replace(id.ToString(), Guid.Empty.ToString())
        })
        {
            using var response = await server.GetPath(invalid, server.Token(group: group));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.True(response.Headers.CacheControl!.NoStore);
        }
        using var anonymous = await server.GetPath(path, null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Theory]
    [InlineData("hash", HttpStatusCode.InternalServerError, "version_data_inconsistent")]
    [InlineData("size", HttpStatusCode.InternalServerError, "version_data_inconsistent")]
    [InlineData("identity", HttpStatusCode.InternalServerError, "version_data_inconsistent")]
    [InlineData("manifest", HttpStatusCode.InternalServerError, "version_data_inconsistent")]
    [InlineData("missing", HttpStatusCode.ServiceUnavailable, "version_source_unavailable")]
    [InlineData("zip", HttpStatusCode.InternalServerError, "version_data_inconsistent")]
    [InlineData("utf8", HttpStatusCode.InternalServerError, "version_data_inconsistent")]
    public async Task ManifestDiff_HTTP_corrupt_or_missing_retained_source_never_returns_partial_diff_or_sensitive_errors(
        string failure, HttpStatusCode status, string code)
    {
        var group = Guid.NewGuid();
        var seed = await Seed(RegistryRole.Viewer, group, false);
        using var storage = new DiffStorage();
        var workflow = seed.Type + "/diff-corrupt";
        var id = await StoreDiffVersion(storage.Store, workflow, "1.0.0", "private-source-literal");
        var sql = failure switch
        {
            "hash" => "UPDATE workflow_versions SET source_sha256=decode(repeat('e',64),'hex'),source_uri='sources/'||repeat('e',64) WHERE id=$1",
            "size" => "UPDATE workflow_versions SET source_size=source_size+1 WHERE id=$1",
            "identity" => "UPDATE workflow_versions SET manifest=jsonb_set(manifest,'{name}','\"team/type/foreign\"') WHERE id=$1",
            "manifest" => "UPDATE workflow_versions SET manifest=jsonb_set(manifest,'{nodes}','null') WHERE id=$1",
            _ => null
        };
        if (sql is not null)
        {
            await using var update = database.DataSource.CreateCommand(sql);
            update.Parameters.AddWithValue(id);
            await update.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        if (failure == "hash")
        {
            var oldContent = DiffArchive(Encoding.UTF8.GetBytes("private-source-literal"));
            await File.WriteAllBytesAsync(Path.Combine(storage.Root, "sources", new string('e', 64)), oldContent,
                TestContext.Current.CancellationToken);
        }
        if (failure == "missing")
        {
            foreach (var file in Directory.GetFiles(Path.Combine(storage.Root, "sources"))) File.Delete(file);
        }
        if (failure is "zip" or "utf8")
        {
            var bytes = failure == "zip" ? new byte[] { 1, 2, 3 } : DiffArchive([0xff, 0xfe]);
            await SetDiffSource(storage.Store, id, bytes);
        }
        await using var server = await Server.Start(database.DataSource,
            configureServices: services => services.AddSingleton<IBlobStore>(storage.Store));
        using var response = await server.GetPath(DiffPath(workflow, id, id), server.Token(group: group));
        Assert.Equal(status, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains(code, body);
        foreach (var secret in new[] { "private-source-literal", storage.Root, "sources/", "stackTrace", "\"before\"" })
            Assert.DoesNotContain(secret, body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ManifestDiff_source_pointer_cannot_be_changed_into_a_filesystem_path_in_PostgreSQL()
    {
        var group = Guid.NewGuid();
        var seed = await Seed(RegistryRole.Viewer, group, false);
        using var storage = new DiffStorage();
        var workflow = seed.Type + "/diff-path";
        var id = await StoreDiffVersion(storage.Store, workflow, "1.0.0", "retained");
        await using var update = database.DataSource.CreateCommand(
            "UPDATE workflow_versions SET source_uri='sources/../../private' WHERE id=$1");
        update.Parameters.AddWithValue(id);
        var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            update.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        Assert.Equal(Npgsql.PostgresErrorCodes.CheckViolation, error.SqlState);
        await using var server = await Server.Start(database.DataSource,
            configureServices: services => services.AddSingleton<IBlobStore>(storage.Store));
        var diff = await ReadDiff(server, workflow, id, id, server.Token(group: group));
        Assert.Equal("retained", diff.Source.Before);
    }

    [Fact]
    public async Task ManifestDiff_HTTP_database_outage_is_explicit_and_openapi_exposes_typed_pair_contract()
    {
        using var storage = new DiffStorage();
        await using (var unavailable = Npgsql.NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Username=unused;Database=unused;Timeout=1"))
        await using (var server = await Server.Start(unavailable,
            configureServices: services => services.AddSingleton<IBlobStore>(storage.Store)))
        {
            using var response = await server.GetPath(DiffPath("team/type/workflow", Guid.CreateVersion7(), Guid.CreateVersion7()),
                server.Token(role: "Admin"));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Contains("version_data_unavailable", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }
        await using var healthy = await Server.Start(database.DataSource);
        using var openapi = await healthy.GetPath("/openapi/v1.json", healthy.Token(role: "Admin"));
        using var document = JsonDocument.Parse(await openapi.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        var operation = document.RootElement.GetProperty("paths")
            .GetProperty("/api/v1/workflows/{team}/{type}/{workflow}/diff").GetProperty("get");
        Assert.Equal("#/components/schemas/WorkflowVersionDiff", operation.GetProperty("responses").GetProperty("200")
            .GetProperty("content").GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
        var queries = operation.GetProperty("parameters").EnumerateArray()
            .Where(parameter => parameter.GetProperty("in").GetString() == "query").ToArray();
        Assert.Equal(["from", "to"], queries.Select(parameter => parameter.GetProperty("name").GetString()));
        Assert.All(queries, parameter => Assert.True(parameter.GetProperty("required").GetBoolean()));
    }

    private async Task<Guid> StoreDiffVersion(IBlobStore store, string workflow, string semver, string source, bool changed = false)
    {
        var id = await StorePlanVersion(workflow, semver);
        await using var read = database.DataSource.CreateCommand("SELECT manifest::text FROM workflow_versions WHERE id=$1");
        read.Parameters.AddWithValue(id);
        var manifest = JsonSerializer.Deserialize((string)(await read.ExecuteScalarAsync(TestContext.Current.CancellationToken))!,
            WorkflowManifestJsonContext.Default.WorkflowManifest)!;
        manifest = manifest with
        {
            Variables = [new("changed", VariableType.String, @default: JsonSerializer.SerializeToElement(changed ? "new" : "old")),
                new(changed ? "added" : "removed", VariableType.Int)],
            RunTimeout = changed ? TimeSpan.FromHours(25) : manifest.RunTimeout,
            Nodes = [.. manifest.Nodes.Select(node => node.Key == "work" ?
                new NodeDefinition(node.Key, node.Dependencies,
                    [new("sdk", policy: new(TimeSpan.FromSeconds(changed ? 24 : 12), new(changed ? 3 : 2)))],
                    inputs: node.Inputs, timeout: node.Timeout, placement: node.Placement) : node),
                new(changed ? "added" : "removed", [], [new("task", policy: new(TimeSpan.FromSeconds(1), new()))])]
        };
        await using var update = database.DataSource.CreateCommand("UPDATE workflow_versions SET manifest=$2 WHERE id=$1");
        update.Parameters.AddWithValue(id);
        update.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(manifest, WorkflowManifestJsonContext.Default.WorkflowManifest));
        await update.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        await SetDiffSource(store, id, DiffArchive(Encoding.UTF8.GetBytes(source)));
        return id;
    }

    private async Task SetDiffSource(IBlobStore store, Guid id, byte[] bytes)
    {
        var content = new BlobContent(BlobKind.Sources, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length);
        using var stream = new MemoryStream(bytes);
        await store.UploadAsync(content, stream, TestContext.Current.CancellationToken);
        await using var update = database.DataSource.CreateCommand(
            "UPDATE workflow_versions SET source_uri=$2,source_sha256=$3,source_size=$4 WHERE id=$1");
        update.Parameters.AddWithValue(id);
        update.Parameters.AddWithValue(content.Key);
        update.Parameters.AddWithValue(Convert.FromHexString(content.Sha256));
        update.Parameters.AddWithValue(content.SizeBytes);
        await update.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static byte[] DiffArchive(byte[] source)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            var entry = archive.CreateEntry("workflow.cs", CompressionLevel.NoCompression);
            entry.LastWriteTime = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
            entry.ExternalAttributes = 0;
            using var stream = entry.Open();
            stream.Write(source);
        }
        return output.ToArray();
    }

    private static string DiffPath(string workflow, Guid from, Guid to) =>
        VersionsPath(workflow)[..^"/versions".Length] + "/diff?from=" + from.ToString("D") + "&to=" + to.ToString("D");

    private static async Task<WorkflowVersionDiff> ReadDiff(Server server, string workflow, Guid from, Guid to, string jwt)
    {
        using var response = await server.GetPath(DiffPath(workflow, from, to), jwt);
        Assert.True(response.StatusCode == HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.True(response.Headers.CacheControl!.NoStore);
        return (await response.Content.ReadFromJsonAsync(VersionsJsonContext.Default.WorkflowVersionDiff,
            TestContext.Current.CancellationToken))!;
    }

    private sealed class DiffStorage : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "wf-diff-" + Guid.NewGuid().ToString("N"));
        public FileSystemBlobStore Store { get; }
        public DiffStorage() => Store = new(Root, RandomNumberGenerator.GetBytes(32));
        public void Dispose()
        {
            Store.Dispose();
            Directory.Delete(Root, recursive: true);
        }
    }
}
