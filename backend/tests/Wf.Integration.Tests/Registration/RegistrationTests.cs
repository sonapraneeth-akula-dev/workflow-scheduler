using System.IdentityModel.Tokens.Jwt;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Microsoft.IdentityModel.Validators;
using Npgsql;
using Wf.Contracts.Manifest;
using Wf.Contracts.Registration;
using Wf.Contracts.Registry;
using Wf.ControlPlane.Auth;
using Wf.ControlPlane.Api;
using Wf.ControlPlane.Data;
using Wf.ControlPlane.Engine.Recovery;
using Wf.ControlPlane.Engine.Registry;
using Wf.ControlPlane.Registration;
using Wf.ControlPlane.Storage;
using Wf.Integration.Tests.Agents;
using Wf.Sdk;

namespace Wf.Integration.Tests.Registration;

public sealed partial class RegistrationTests(RegistrationDatabase database) : IClassFixture<RegistrationDatabase>
{
    [Fact]
    public async Task Http_registration_persists_real_sdk_manifest_source_package_provenance_active_pointer_audit_and_notify()
    {
        await using var server = await Server.Start(database.DataSource);
        var request = await Request(server);
        var notified = false;
        await using var listener = await database.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        listener.Notification += (_, args) => notified |= args.Channel == "registry" && args.Payload == request.WorkflowId;
        await using (var listen = new NpgsqlCommand("LISTEN registry", listener))
            await listen.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        using var response = await server.Post(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await Json(response);
        Assert.Equal(["registrySnapshot", "workflowVersion"], json.EnumerateObject().Select(property => property.Name).Order().ToArray());
        var version = json.GetProperty("workflowVersion").GetGuid();
        Assert.Equal(version, await Value("SELECT active_version_id FROM workflows WHERE id=$1", request.WorkflowId!));
        Assert.Equal(version, await Value("SELECT latest_version_id FROM workflows WHERE id=$1", request.WorkflowId!));
        await using (var query = database.DataSource.CreateCommand("""
            SELECT manifest::text,git_commit,git_author,registered_by,change_note,sdk_version,
                package_uri,package_sha256,package_size,source_uri,source_sha256,source_size
            FROM workflow_versions WHERE id=$1
            """))
        {
            query.Parameters.AddWithValue(version);
            await using var reader = await query.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
            using var stored = JsonDocument.Parse(reader.GetString(0));
            Assert.Equal(request.WorkflowId, stored.RootElement.GetProperty("name").GetString());
            Assert.Equal(request.GitCommit, reader.GetString(1));
            Assert.Equal(request.GitAuthor, reader.GetString(2));
            Assert.Equal(Server.Actor.ToString(), reader.GetString(3));
            Assert.Equal(request.ChangeNote, reader.GetString(4));
            Assert.False(string.IsNullOrWhiteSpace(reader.GetString(5)));
            Assert.Equal("packages/" + request.Package!.Sha256 + ".zip", reader.GetString(6));
            Assert.Equal(Convert.FromHexString(request.Package.Sha256), reader.GetFieldValue<byte[]>(7));
            Assert.Equal(request.Package.Size, reader.GetInt64(8));
            var source = new BlobContent(BlobKind.Sources, Convert.ToHexStringLower(reader.GetFieldValue<byte[]>(10)), reader.GetInt64(11));
            Assert.Equal(source.Key, reader.GetString(9));
            using var zip = new MemoryStream();
            await server.Blobs.DownloadAsync(source, zip, TestContext.Current.CancellationToken);
            zip.Position = 0;
            using var archive = new ZipArchive(zip, ZipArchiveMode.Read);
            using var sourceReader = new StreamReader(Assert.Single(archive.Entries).Open());
            Assert.Equal(request.Source, await sourceReader.ReadToEndAsync(TestContext.Current.CancellationToken));
        }
        Assert.Equal(1L, await Value("SELECT count(*) FROM registry_snapshots WHERE team_id=$1", Team(request)));
        Assert.Equal(1L, await Value("SELECT count(*) FROM audit_log WHERE target_id=$1 AND actor=$2 AND actor_kind='App'",
            request.WorkflowId!, Server.Actor.ToString()));
        var summary = Assert.IsType<string>(await Value("SELECT summary::text FROM audit_log WHERE target_id=$1", request.WorkflowId!));
        Assert.Contains(version.ToString(), summary);
        Assert.DoesNotContain(request.Source!, summary);
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!notified) await listener.WaitAsync(wait.Token);
        Assert.True(notified);
    }

    [Fact]
    public async Task Identical_registration_is_200_without_version_row_rewrites_history_or_audit()
    {
        await using var server = await Server.Start(database.DataSource);
        var request = await Request(server);
        using var first = await server.Post(request);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var version = (await Json(first)).GetProperty("workflowVersion").GetGuid();
        var before = await Value("SELECT xmin::text FROM workflow_versions WHERE id=$1", version);
        using var second = await server.Post(request with { GitCommit = new('b', 40), GitAuthor = "Different provenance", ChangeNote = "No new version" });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Empty((await Json(second)).EnumerateObject());
        Assert.Equal(before, await Value("SELECT xmin::text FROM workflow_versions WHERE id=$1", version));
        Assert.Equal(request.GitCommit, await Value("SELECT git_commit FROM workflow_versions WHERE id=$1", version));
        Assert.Equal(1L, await Value("SELECT count(*) FROM workflow_versions WHERE workflow_id=$1", request.WorkflowId!));
        Assert.Equal(1L, await Value("SELECT count(*) FROM registry_snapshots WHERE team_id=$1", Team(request)));
        Assert.Equal(1L, await Value("SELECT count(*) FROM audit_log WHERE target_id=$1", request.WorkflowId!));
    }

    [Fact]
    public async Task Registry_only_change_returns_only_snapshot_and_preserves_active_version()
    {
        await using var server = await Server.Start(database.DataSource);
        var request = await Request(server);
        using var first = await server.Post(request);
        var version = (await Json(first)).GetProperty("workflowVersion").GetGuid();
        var group = Guid.NewGuid();
        var registry = request.Registry!.Value.Deserialize(TeamRegistryJsonContext.Default.TeamRegistry)! with
        {
            RoleBindings = [new(RegistryRole.Operator, group)]
        };
        using var next = await server.Post(new(request.GitCommit, request.GitAuthor,
            Registry: JsonSerializer.SerializeToElement(registry, TeamRegistryJsonContext.Default.TeamRegistry)));
        Assert.Equal(HttpStatusCode.Created, next.StatusCode);
        Assert.Equal("registrySnapshot", Assert.Single((await Json(next)).EnumerateObject()).Name);
        Assert.Equal(version, await Value("SELECT active_version_id FROM workflows WHERE id=$1", request.WorkflowId!));
        Assert.Equal(1L, await Value("SELECT count(*) FROM workflow_versions WHERE workflow_id=$1", request.WorkflowId!));
        Assert.Equal(1L, await Value("SELECT count(*) FROM role_bindings WHERE scope_id=$1 AND role=1 AND principal_id=$2", Team(request), group));
    }

    [Fact]
    public async Task New_patch_is_active_while_previous_version_and_pinned_run_remain_unchanged()
    {
        await using var server = await Server.Start(database.DataSource);
        var first = await Request(server);
        using var registered = await server.Post(first);
        var oldId = (await Json(registered)).GetProperty("workflowVersion").GetGuid();
        var run = Guid.CreateVersion7();
        await Execute("""
            INSERT INTO runs(run_day,id,workflow_id,version_id,trigger,triggered_by,variables,metadata,status,created_at)
            VALUES(uuid_v7_run_day($1),$1,$2,$3,0,'test','{}','{}',0,now())
            """, run, first.WorkflowId!, oldId);
        var updated = first with { Manifest = Manifest(first.WorkflowId!, "1.0.1"), Source = "// updated source" };
        using var response = await server.Post(updated);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await Json(response);
        Assert.Equal("workflowVersion", Assert.Single(json.EnumerateObject()).Name);
        var newId = json.GetProperty("workflowVersion").GetGuid();
        Assert.NotEqual(oldId, newId);
        Assert.Equal(newId, await Value("SELECT active_version_id FROM workflows WHERE id=$1", first.WorkflowId!));
        Assert.Equal(oldId, await Value("SELECT version_id FROM runs WHERE id=$1", run));
        Assert.Equal(2L, await Value("SELECT count(*) FROM workflow_versions WHERE workflow_id=$1", first.WorkflowId!));
        using var repeatOld = await server.Post(first);
        Assert.Equal(HttpStatusCode.OK, repeatOld.StatusCode);
        Assert.Equal(newId, await Value("SELECT active_version_id FROM workflows WHERE id=$1", first.WorkflowId!));
    }

    [Fact]
    public async Task Breaking_patch_and_immutable_version_collision_roll_back_registry_roles_history_and_activation()
    {
        await using var server = await Server.Start(database.DataSource);
        var first = await Request(server);
        using var registered = await server.Post(first);
        var oldId = (await Json(registered)).GetProperty("workflowVersion").GetGuid();
        var group = Guid.NewGuid();
        var changedRegistry = first.Registry!.Value.Deserialize(TeamRegistryJsonContext.Default.TeamRegistry)! with
        {
            RoleBindings = [new(RegistryRole.Admin, group)]
        };
        var manifest = Manifest(first.WorkflowId!, "1.0.1").Deserialize(WorkflowManifestJsonContext.Default.WorkflowManifest)! with { Nodes = [] };
        using var breaking = await server.Post(first with
        {
            Manifest = JsonSerializer.SerializeToElement(manifest, WorkflowManifestJsonContext.Default.WorkflowManifest),
            Registry = JsonSerializer.SerializeToElement(changedRegistry, TeamRegistryJsonContext.Default.TeamRegistry)
        });
        Assert.Equal(HttpStatusCode.Conflict, breaking.StatusCode);
        var error = Assert.Single((await Json(breaking)).GetProperty("errors").EnumerateArray());
        Assert.Equal("WFV010", error.GetProperty("ruleId").GetString());
        Assert.Contains("MAJOR", error.GetProperty("hint").GetString());
        using var collision = await server.Post(first with { Source = "// different immutable source", Registry = first.Registry });
        Assert.Equal(HttpStatusCode.Conflict, collision.StatusCode);
        Assert.Equal(oldId, await Value("SELECT active_version_id FROM workflows WHERE id=$1", first.WorkflowId!));
        Assert.Equal(0L, await Value("SELECT count(*) FROM role_bindings WHERE scope_id=$1 AND principal_id=$2", Team(first), group));
        Assert.Equal(1L, await Value("SELECT count(*) FROM registry_snapshots WHERE team_id=$1", Team(first)));
        Assert.Equal(1L, await Value("SELECT count(*) FROM audit_log WHERE target_id=$1", first.WorkflowId!));
    }

    [Fact]
    public async Task Semver_baseline_uses_latest_registration_even_if_active_version_was_rolled_back()
    {
        await using var server = await Server.Start(database.DataSource);
        var first = await Request(server);
        using var old = await server.Post(first);
        var oldId = (await Json(old)).GetProperty("workflowVersion").GetGuid();
        using var latest = await server.Post(first with { Manifest = Manifest(first.WorkflowId!, "1.1.0") });
        Assert.Equal(HttpStatusCode.Created, latest.StatusCode);
        var latestId = (await Json(latest)).GetProperty("workflowVersion").GetGuid();
        await Execute("UPDATE workflows SET active_version_id=$2 WHERE id=$1", first.WorkflowId!, oldId);
        using var response = await server.Post(first with { Manifest = Manifest(first.WorkflowId!, "1.0.1") });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(latestId, await Value("SELECT latest_version_id FROM workflows WHERE id=$1", first.WorkflowId!));
        Assert.Equal(oldId, await Value("SELECT active_version_id FROM workflows WHERE id=$1", first.WorkflowId!));
    }

    [Fact]
    public async Task Concurrent_identical_registrations_produce_one_version_snapshot_audit_and_created_response()
    {
        await using var server = await Server.Start(database.DataSource);
        for (var round = 0; round < 8; round++)
        {
            var request = await Request(server);
            var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => server.Post(request)));
            try
            {
                Assert.True(responses.All(response => response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK),
                    string.Join(",", responses.Select(response => response.StatusCode)) + Environment.NewLine +
                    string.Join(Environment.NewLine, server.Failures));
                Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
                Assert.Equal(11, responses.Count(response => response.StatusCode == HttpStatusCode.OK));
                Assert.Equal(1L, await Value("SELECT count(*) FROM workflow_versions WHERE workflow_id=$1", request.WorkflowId!));
                Assert.Equal(1L, await Value("SELECT count(*) FROM registry_snapshots WHERE team_id=$1", Team(request)));
                Assert.Equal(1L, await Value("SELECT count(*) FROM audit_log WHERE target_id=$1", request.WorkflowId!));
            }
            finally { foreach (var response in responses) response.Dispose(); }
        }
    }

    [Fact]
    public async Task Audit_failure_prevents_any_registry_workflow_or_binding_commit()
    {
        await using var server = await Server.Start(database.DataSource);
        var request = await Request(server);
        await Execute("""
            CREATE FUNCTION reject_registration_audit() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN IF NEW.target_id=TG_ARGV[0] THEN RAISE EXCEPTION 'audit unavailable'; END IF; RETURN NEW; END $$;
            """);
        await using (var trigger = database.DataSource.CreateCommand($"""
            CREATE TRIGGER reject_registration_audit BEFORE INSERT ON audit_log
            FOR EACH ROW EXECUTE FUNCTION reject_registration_audit('{request.WorkflowId}')
            """))
            await trigger.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        using var response = await server.Post(request);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(0L, await Value("SELECT count(*) FROM workflows WHERE id=$1", request.WorkflowId!));
        Assert.Equal(0L, await Value("SELECT count(*) FROM teams WHERE id=$1", Team(request)));
        Assert.Equal(0L, await Value("SELECT count(*) FROM registry_snapshots WHERE team_id=$1", Team(request)));
    }

    [Fact]
    public async Task Removing_in_use_type_is_a_conflict_and_rolls_back_whole_registry_snapshot()
    {
        await using var server = await Server.Start(database.DataSource);
        var request = await Request(server);
        using var registered = await server.Post(request);
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        var empty = JsonSerializer.SerializeToElement(new TeamRegistry(Team(request), []), TeamRegistryJsonContext.Default.TeamRegistry);
        using var response = await server.Post(new(request.GitCommit, request.GitAuthor, Registry: empty));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(1L, await Value("SELECT count(*) FROM workflow_types WHERE team_id=$1", Team(request)));
        Assert.Equal(1L, await Value("SELECT count(*) FROM registry_snapshots WHERE team_id=$1", Team(request)));
    }

    [Fact]
    public async Task Actual_http_grants_upload_replay_and_exists_response_verify_stored_bytes()
    {
        await using var server = await Server.Start(database.DataSource);
        var bytes = Encoding.UTF8.GetBytes("verified package bytes");
        var package = Reference(bytes);
        using var created = await server.Grant(package);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var grant = await created.Content.ReadFromJsonAsync(RegistrationJsonContext.Default.UploadGrantResponse, TestContext.Current.CancellationToken);
        Assert.False(grant!.Exists);
        Assert.Equal(package.Size, grant.MaxBytes);
        Assert.StartsWith("/api/v1/uploads/", grant.UploadUri);
        using (var uploaded = await server.Upload(grant.UploadUri!, bytes)) Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
        using (var replay = await server.Upload(grant.UploadUri!, bytes)) Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        using var exists = await server.Grant(package);
        Assert.Equal(HttpStatusCode.OK, exists.StatusCode);
        Assert.True((await Json(exists)).GetProperty("exists").GetBoolean());
        var file = Path.Combine(server.Root, "packages", package.Sha256 + ".zip");
        await File.WriteAllBytesAsync(file, Encoding.UTF8.GetBytes("tampered package bytes"), TestContext.Current.CancellationToken);
        using var corrupt = await server.Grant(package);
        Assert.Equal(HttpStatusCode.Conflict, corrupt.StatusCode);
    }

    [Theory]
    [InlineData("package", "bad", 10, 400)]
    [InlineData("sources", "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", 10, 400)]
    [InlineData("package", "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", 0, 400)]
    [InlineData("package", "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", -1, 400)]
    [InlineData("package", "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", 536870913, 413)]
    public async Task Upload_grant_boundary_rejects_unsupported_kind_digest_and_sizes(string kind, string sha, long size, int status)
    {
        await using var server = await Server.Start(database.DataSource);
        using var content = JsonContent.Create(new UploadGrantRequest(kind, sha, size), RegistrationJsonContext.Default.UploadGrantRequest);
        using var response = await server.Send(HttpMethod.Post, "/api/v1/upload-grants", content);
        Assert.Equal(status, (int)response.StatusCode);
    }

    [Theory]
    [InlineData("oversize", 413)]
    [InlineData("chunked-oversize", 413)]
    [InlineData("short", 400)]
    [InlineData("wrong-hash", 400)]
    [InlineData("tampered-grant", 403)]
    [InlineData("expired", 403)]
    public async Task Raw_upload_rejects_invalid_content_and_grants_without_publishing_partial_bytes(string mode, int status)
    {
        await using var server = await Server.Start(database.DataSource);
        var bytes = Encoding.UTF8.GetBytes("abc");
        var package = Reference(bytes);
        using var created = await server.Grant(package);
        var url = (await Json(created)).GetProperty("uploadUri").GetString()!;
        if (mode == "tampered-grant")
        {
            var signature = url.LastIndexOf('.') + 1;
            url = url[..signature] + (url[signature] == 'A' ? 'B' : 'A') + url[(signature + 1)..];
        }
        if (mode == "expired") server.Clock.Now = server.Clock.Now.AddMinutes(11);
        var uploaded = mode switch
        {
            "oversize" or "chunked-oversize" => Encoding.UTF8.GetBytes("abcd"),
            "short" => Encoding.UTF8.GetBytes("ab"),
            "wrong-hash" => Encoding.UTF8.GetBytes("xyz"),
            _ => bytes
        };
        using var response = await server.Upload(url, uploaded, chunked: mode == "chunked-oversize");
        Assert.Equal(status, (int)response.StatusCode);
        Assert.False(File.Exists(Path.Combine(server.Root, "packages", package.Sha256 + ".zip")));
        Assert.Empty(Directory.EnumerateFiles(server.Root, ".upload-*"));
    }

    [Theory]
    [InlineData(null, "app", null, 401)]
    [InlineData("Agent", "app", null, 403)]
    [InlineData("Registrar", "user", "api.read", 403)]
    [InlineData("Registrar", "app", "api.read", 403)]
    [InlineData("Registrar", "app", null, 201)]
    public async Task Registration_and_grants_require_an_authenticated_app_only_registrar(string? role, string identity, string? scope, int expected)
    {
        await using var server = await Server.Start(database.DataSource);
        var request = await Request(server);
        var token = role is null ? null : server.Token(role, identity, scope);
        using var registration = await server.Post(request, token, useDefaultToken: false);
        Assert.Equal(expected, (int)registration.StatusCode);
        using var grantContent = JsonContent.Create(new UploadGrantRequest("package", Reference([1, 2]).Sha256, 2),
            RegistrationJsonContext.Default.UploadGrantRequest);
        using var grant = await server.Send(HttpMethod.Post, "/api/v1/upload-grants", grantContent, token, useDefaultToken: false);
        Assert.Equal(expected, (int)grant.StatusCode);
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("workflow-id")]
    [InlineData("manifest-identity")]
    [InlineData("git")]
    [InlineData("source")]
    [InlineData("package")]
    [InlineData("metadata")]
    public async Task Invalid_registration_fails_before_any_database_mutation(string invalid)
    {
        await using var server = await Server.Start(database.DataSource);
        var request = await Request(server);
        request = invalid switch
        {
            "workflow-id" => request with { WorkflowId = "../type/name" },
            "manifest-identity" => request with { Manifest = Manifest("other/release/workflow", "1.0.0") },
            "git" => request with { GitCommit = "not-a-commit" },
            "source" => request with { Source = new('s', 1024 * 1024 + 1) },
            "package" => request with { Package = new(new('0', 64), 123) },
            "metadata" => request with { Manifest = Manifest(request.WorkflowId!, "1.0.0", invalidMetadata: true) },
            _ => request
        };
        using var response = await server.Post(request, invalid == "actor" ? server.Token(includeActor: false) : null);
        Assert.Equal(invalid == "actor" ? HttpStatusCode.Forbidden : HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0L, await Value("SELECT count(*) FROM teams WHERE id=$1", Team(request)));
        Assert.Equal(0L, await Value("SELECT count(*) FROM workflow_versions WHERE workflow_id=$1", request.WorkflowId!));
    }

    [Theory]
    [InlineData("{\"gitCommit\":\"a\",\"gitAuthor\":\"b\",\"registeredBy\":\"spoofed\"}")]
    [InlineData("{\"gitCommit\":\"a\",\"gitCommit\":\"b\",\"gitAuthor\":\"c\"}")]
    [InlineData("{\"gitCommit\":null,\"gitAuthor\":\"b\"}")]
    public async Task Strict_http_contract_rejects_unknown_duplicate_and_null_fields_without_echo(string json)
    {
        await using var server = await Server.Start(database.DataSource);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await server.Send(HttpMethod.Post, "/api/v1/registrations", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("spoofed", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("unknown-property")]
    [InlineData("duplicate-version")]
    [InlineData("missing-nodes")]
    [InlineData("null-metadata")]
    [InlineData("array-metadata")]
    [InlineData("invalid-nodes-object")]
    [InlineData("invalid-policy")]
    [InlineData("oversized-version")]
    public async Task Raw_manifest_backstop_rejects_unknown_missing_null_and_malformed_contracts(string invalid)
    {
        await using var server = await Server.Start(database.DataSource);
        var request = await Request(server);
        var raw = request.Manifest!.Value.GetRawText();
        var node = JsonNode.Parse(raw)!;
        switch (invalid)
        {
            case "unknown-property": node["resolvedSecret"] = "never-echo-this-value"; break;
            case "missing-nodes": node.AsObject().Remove("nodes"); break;
            case "null-metadata": node["metadata"] = null; break;
            case "array-metadata": node["metadata"] = new JsonArray(); break;
            case "invalid-nodes-object": node["nodes"] = new JsonObject(); break;
            case "invalid-policy": node["nodes"]![0]!["tasks"]![0]!["policy"]!["attemptTimeout"] = "30.00:00:00"; break;
            case "oversized-version": node["version"] = new string('9', 257) + ".0.0"; break;
        }
        using var document = JsonDocument.Parse(invalid == "duplicate-version"
            ? raw[..^1] + ",\"version\":\"99.0.0\"}" : node.ToJsonString());
        using var response = await server.Post(request with { Manifest = document.RootElement.Clone() });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("never-echo-this-value", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0L, await Value("SELECT count(*) FROM teams WHERE id=$1", Team(request)));
    }

    [Theory]
    [InlineData("/api/v1/registrations", "gitCommit", 8388708)]
    [InlineData("/api/v1/upload-grants", "sha256", 4196)]
    public async Task Actual_http_json_body_limit_is_enforced_before_deserialization_completes(string route, string property, int bytes)
    {
        await using var server = await Server.Start(database.DataSource);
        using var content = new StringContent("{\"" + property + "\":\"" + new string('a', bytes) + "\"}", Encoding.UTF8, "application/json");
        using var response = await server.Send(HttpMethod.Post, route, content);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/v1/registrations")]
    [InlineData("/api/v1/upload-grants")]
    public async Task Unsupported_json_content_type_is_415_instead_of_an_unhandled_server_error(string route)
    {
        await using var server = await Server.Start(database.DataSource);
        using var content = new StringContent("{}", Encoding.UTF8, "text/plain");
        using var response = await server.Send(HttpMethod.Post, route, content);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Theory]
    [InlineData("1.0.0-alpha.1+build")]
    [InlineData("999999999999999999999999.0.0")]
    public async Task Semver_storage_preserves_prereleases_build_metadata_and_large_numeric_components(string version)
    {
        await using var server = await Server.Start(database.DataSource);
        var request = await Request(server);
        using var response = await server.Post(request with { Manifest = Manifest(request.WorkflowId!, version) });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await Json(response)).GetProperty("workflowVersion").GetGuid();
        Assert.Equal(version, await Value("SELECT semver FROM workflow_versions WHERE id=$1", id));
        Assert.Equal(version.Split('+')[0], await Value("SELECT semver_key FROM workflow_versions WHERE id=$1", id));
    }

    [Fact]
    public async Task New_schema_has_13_monthly_audit_partitions_and_active_pointer_cannot_cross_workflows()
    {
        Assert.Equal(13L, await Value("""
            SELECT count(*) FROM pg_inherits WHERE inhparent='audit_log'::regclass
            """));
        await using var server = await Server.Start(database.DataSource);
        var first = await Request(server);
        var second = await Request(server);
        using var a = await server.Post(first);
        using var b = await server.Post(second);
        var foreignVersion = (await Json(b)).GetProperty("workflowVersion").GetGuid();
        var error = await Assert.ThrowsAsync<PostgresException>(() => Execute(
            "UPDATE workflows SET active_version_id=$2 WHERE id=$1", first.WorkflowId!, foreignVersion));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
    }

    private static string Team(RegistrationRequest request) => request.Registry!.Value.GetProperty("team").GetString()!;

    internal static JsonElement Manifest(string id, string version, bool invalidMetadata = false)
    {
        var workflow = Workflow.Define(id, version, builder =>
        {
            builder.Type("release").Node("work", node => node.Task("task", _ => { }));
            if (invalidMetadata) builder.Metadata("undeclared", "must-not-be-echoed");
        });
        using var document = JsonDocument.Parse(ManifestWriter.WriteUtf8(workflow));
        return document.RootElement.Clone();
    }

    internal static async Task<RegistrationRequest> Request(Server server)
    {
        var team = "registration-" + Guid.NewGuid().ToString("N");
        var id = team + "/release/workflow";
        var bytes = Encoding.UTF8.GetBytes("package-" + id);
        var package = Reference(bytes);
        using var grant = await server.Grant(package);
        Assert.Equal(HttpStatusCode.Created, grant.StatusCode);
        using var upload = await server.Upload((await Json(grant)).GetProperty("uploadUri").GetString()!, bytes);
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        var registry = new TeamRegistry(team, [new(team, "release", "description", new([]))]);
        return new(new('a', 40), "Integration Author", id, Manifest(id, "1.0.0"), package,
            "// workflow source " + id, JsonSerializer.SerializeToElement(registry, TeamRegistryJsonContext.Default.TeamRegistry), "Initial registration");
    }

    private static PackageReference Reference(byte[] bytes) => new(Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length);

    internal static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        return document.RootElement.Clone();
    }

    private async Task<object?> Value(string sql, params object[] parameters)
    {
        await using var query = database.DataSource.CreateCommand(sql);
        foreach (var value in parameters) query.Parameters.AddWithValue(value);
        return await query.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }

    private async Task Execute(string sql, params object[] parameters)
    {
        await using var query = database.DataSource.CreateCommand(sql);
        foreach (var value in parameters) query.Parameters.AddWithValue(value);
        await query.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    internal sealed class Server(WebApplication app, HttpClient client, RSA rsa, RsaSecurityKey key, string root, Clock clock,
        ConcurrentQueue<Exception> failures) : IAsyncDisposable
    {
        private const string Tenant = "ef8a05a0-451c-4479-9c8d-391b6bc7ee1a";
        private const string Audience = "562b0870-4522-43da-8587-41e9b788f309";
        private const string Issuer = $"https://login.microsoftonline.com/{Tenant}/v2.0";
        public static readonly Guid Actor = Guid.Parse("44cd65e3-f210-4201-b5a8-74321260704a");
        public string Root => root;
        public Clock Clock => clock;
        public IBlobStore Blobs => app.Services.GetRequiredService<IBlobStore>();
        public ActiveVersionCache Versions => app.Services.GetRequiredService<ActiveVersionCache>();
        public ConcurrentQueue<Exception> Failures => failures;

        public static async Task<Server> Start(NpgsqlDataSource source, bool runs = false, bool userSchedules = false)
        {
            runs |= userSchedules;
            var root = Path.Combine(Path.GetTempPath(), "wf-registration-" + Guid.NewGuid().ToString("N"));
            var rsa = RSA.Create(2048);
            var key = new RsaSecurityKey(rsa) { KeyId = "registration-test" };
            var clock = new Clock();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Configuration["AzureAd:TenantId"] = Tenant;
            builder.Configuration["AzureAd:ClientId"] = Audience;
            if (userSchedules)
            {
                builder.Configuration["AzureAd:SpaClientId"] = "60d24b19-f65a-4ed4-96cc-9174bfcacabc";
                builder.Services.AddUserSchedules(builder.Configuration);
                builder.Services.AddWorkflowOpenApi();
            }
            builder.Configuration["Storage:UploadHmacKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            builder.Configuration["Storage:Root"] = root;
            builder.Services.AddWorkflowAuthentication(builder.Configuration, builder.Environment);
            builder.Services.AddSingleton(source);
            builder.Services.AddSingleton<TimeProvider>(clock);
            builder.Services.AddWorkflowRegistrations(builder.Configuration);
            if (runs)
            {
                builder.Services.AddTeamAuthorization();
                builder.Services.AddSingleton<CommandInbox>();
                builder.Services.AddSingleton<ActiveVersionCache>();
                builder.Services.AddHostedService<ActiveVersionCache>(services => services.GetRequiredService<ActiveVersionCache>());
            }
            var metadata = new OpenIdConnectConfiguration { Issuer = Issuer };
            metadata.SigningKeys.Add(key);
            var manager = new StaticConfigurationManager<OpenIdConnectConfiguration>(metadata);
            builder.Services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.Configuration = metadata;
                options.ConfigurationManager = manager;
                options.TokenValidationParameters.IssuerValidator =
                    AadIssuerValidator.GetAadIssuerValidator(Issuer, null, _ => manager).Validate;
                options.TokenValidationParameters.ClockSkew = TimeSpan.Zero;
            });
            var app = builder.Build();
            var failures = new ConcurrentQueue<Exception>();
            app.Use(async (_, next) =>
            {
                try { await next(); }
                catch (Exception error) { failures.Enqueue(error); throw; }
            });
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapRegistrationEndpoints();
            if (runs)
            {
                app.MapRunsEndpoints();
                app.MapSchedulesEndpoints();
                app.MapSeriesFiresEndpoints();
            }
            if (userSchedules)
            {
                app.MapUserSchedulesEndpoints();
                app.MapWorkflowOpenApi();
            }
            await app.StartAsync(TestContext.Current.CancellationToken);
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return new(app, new HttpClient { BaseAddress = new(address), Timeout = TimeSpan.FromSeconds(20) }, rsa, key, root, clock, failures);
        }

        public string Token(string role = "Registrar", string identity = "app", string? scope = null, bool includeActor = true,
            Guid[]? groups = null, Guid? actor = null, string? authorizedClient = "60d24b19-f65a-4ed4-96cc-9174bfcacabc",
            bool includeIdentity = true)
        {
            var claims = new List<Claim>
            {
                new("tid", Tenant), new("ver", "2.0"), new("roles", role)
            };
            if (includeIdentity) claims.Add(new("idtyp", identity));
            if (authorizedClient is not null) claims.Add(new("azp", authorizedClient));
            if (includeActor) claims.Add(new("oid", (actor ?? Actor).ToString()));
            if (scope is not null) claims.Add(new("scp", scope));
            if (groups is not null) claims.AddRange(groups.Select(group => new Claim("groups", group.ToString("D"))));
            return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(Issuer, Audience, claims,
                DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(20), new SigningCredentials(key, SecurityAlgorithms.RsaSha256)));
        }

        public Task<HttpResponseMessage> Post(RegistrationRequest request, string? token = null, bool useDefaultToken = true) =>
            Send(HttpMethod.Post, "/api/v1/registrations",
                JsonContent.Create(request, RegistrationJsonContext.Default.RegistrationRequest), token, useDefaultToken);

        public Task<HttpResponseMessage> Grant(PackageReference package) => Send(HttpMethod.Post, "/api/v1/upload-grants",
            JsonContent.Create(new UploadGrantRequest("package", package.Sha256, package.Size), RegistrationJsonContext.Default.UploadGrantRequest));

        public Task<HttpResponseMessage> Upload(string uri, byte[] bytes, bool chunked = false)
        {
            HttpContent content = chunked ? new StreamContent(new ChunkedStream(bytes)) : new ByteArrayContent(bytes);
            return Send(HttpMethod.Put, uri, content, useDefaultToken: false);
        }

        public async Task<HttpResponseMessage> Send(HttpMethod method, string path, HttpContent content,
            string? token = null, bool useDefaultToken = true, IEnumerable<string>? idempotencyKeys = null,
            IEnumerable<string>? ifMatch = null)
        {
            using var request = new HttpRequestMessage(method, path) { Content = content };
            token ??= useDefaultToken ? Token() : null;
            if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (idempotencyKeys is not null) request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKeys);
            if (ifMatch is not null) request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
            return await client.SendAsync(request, TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await app.DisposeAsync();
            rsa.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    internal sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class ChunkedStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public override bool CanSeek => false;
    }
}

public sealed class RegistrationDatabase : AgentDatabase
{
    public string ConnectionString => DatabaseConnectionString;
    protected override IReadOnlyList<string> Migrations => Migrator.LoadScripts().Select(script => script.Name).ToArray();
}
