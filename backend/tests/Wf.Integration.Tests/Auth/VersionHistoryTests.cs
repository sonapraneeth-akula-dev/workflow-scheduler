using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Wf.Contracts.Registry;
using Wf.ControlPlane.Api;

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
    public async Task Versions_history_and_detail_return_safe_stored_provenance_for_each_viewer_grant(
        RegistryRole role, bool teamScope)
    {
        var group = Guid.NewGuid();
        var seed = await Seed(role, group, teamScope);
        var workflow = seed.Type + "/versions";
        var id = await StorePlanVersion(workflow, "1.2.3-rc.1+build");
        await using (var update = database.DataSource.CreateCommand("""
            UPDATE workflow_versions SET git_author=$2,registered_by=$3,change_note=$4 WHERE id=$1
            """))
        {
            update.Parameters.AddWithValue(id);
            update.Parameters.AddWithValue("Git author <author@example.test>");
            update.Parameters.AddWithValue("immutable registering identity");
            update.Parameters.AddWithValue("Release <script>display as text</script>");
            await update.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        await using var server = await Server.Start(database.DataSource);
        var jwt = server.Token(group: group);
        using var read = await server.GetPath(VersionsPath(workflow), jwt);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.True(read.Headers.CacheControl!.NoStore);
        var body = await read.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var page = JsonSerializer.Deserialize(body, VersionsJsonContext.Default.WorkflowVersionPage)!;
        var summary = Assert.Single(page.Items);
        Assert.Equal(id, summary.VersionId);
        Assert.Equal(workflow, summary.WorkflowId);
        Assert.Equal("1.2.3-rc.1+build", summary.Version);
        Assert.Equal(id, page.ActiveVersionId);
        Assert.Equal(id, page.LatestVersionId);
        Assert.Null(page.NextCursor);
        Assert.Equal(new string('c', 40), summary.Registration.GitCommit);
        Assert.Equal("Git author <author@example.test>", summary.Registration.GitAuthor);
        Assert.Equal("immutable registering identity", summary.Registration.RegisteredBy);
        Assert.Equal("Release <script>display as text</script>", summary.Registration.ChangeNote);
        Assert.Equal("1.0.0", summary.Registration.SdkVersion);
        Assert.Equal(TimeSpan.Zero, summary.Registration.RegisteredAt.Offset);
        using var detail = await server.GetPath(VersionPath(workflow, id), jwt);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        var plan = await detail.Content.ReadFromJsonAsync(VersionsJsonContext.Default.WorkflowVersionPlan,
            TestContext.Current.CancellationToken);
        Assert.Equal(summary.Registration, plan!.Registration);
        foreach (var secret in new[] { "plan-default-secret", "plan-argument-secret", "plan-header-secret",
                     "plan-body-secret", "plan-query-secret", "packageUri", "sourceUri", "sha256", "\"manifest\"", "\"variables\"" })
            Assert.DoesNotContain(secret, body, StringComparison.OrdinalIgnoreCase);
        using var foreign = await server.GetPath(VersionsPath(seed.OtherType + "/versions"), jwt);
        await Check(foreign, false);
    }

    [Fact]
    public async Task Versions_history_keyset_handles_timestamp_ties_new_registrations_rollback_and_deleted_workflows()
    {
        var group = Guid.NewGuid();
        var seed = await Seed(RegistryRole.Viewer, group, false);
        var workflow = seed.Type + "/paged";
        var ids = new List<Guid>();
        for (var index = 0; index < 5; index++)
            ids.Add(await StorePlanVersion(workflow, "1.0." + index));
        var timestamp = new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero).AddTicks(1234560);
        await SetVersionTimes(workflow, timestamp);
        var expected = ids.OrderByDescending(id => id.ToString("D"), StringComparer.Ordinal).ToArray();
        await using var server = await Server.Start(database.DataSource);
        var jwt = server.Token(group: group);
        var path = VersionsPath(workflow);
        var first = await VersionPage(server, path + "?limit=2", jwt);
        Assert.Equal(expected[..2], first.Items.Select(item => item.VersionId));
        Assert.NotNull(first.NextCursor);
        Assert.All(first.Items, item => Assert.Null(item.Registration.ChangeNote));
        var newer = await StorePlanVersion(workflow, "1.0.5");
        await SetVersionTimes(workflow, timestamp.AddSeconds(1), newer);
        await using (var rollback = database.DataSource.CreateCommand(
            "UPDATE workflows SET active_version_id=$2,deleted_at=now() WHERE id=$1"))
        {
            rollback.Parameters.AddWithValue(workflow);
            rollback.Parameters.AddWithValue(ids[0]);
            await rollback.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        var seen = first.Items.Select(item => item.VersionId).ToList();
        var cursor = first.NextCursor;
        while (cursor is not null)
        {
            var page = await VersionPage(server, path + "?limit=2&cursor=" + Uri.EscapeDataString(cursor), jwt);
            Assert.Equal(ids[0], page.ActiveVersionId);
            Assert.Equal(newer, page.LatestVersionId);
            seen.AddRange(page.Items.Select(item => item.VersionId));
            cursor = page.NextCursor;
        }
        Assert.Equal(expected, seen);
        Assert.Equal(seen.Count, seen.Distinct().Count());
        var current = await VersionPage(server, path + "?limit=100", jwt);
        Assert.Equal(newer, current.Items[0].VersionId);
        Assert.Equal(6, current.Items.Length);
        Assert.Null(current.NextCursor);
        using var retained = await server.GetPath(VersionPath(workflow, ids[0]), jwt);
        Assert.Equal(HttpStatusCode.OK, retained.StatusCode);
        using var indexQuery = database.DataSource.CreateCommand(
            "SELECT indexdef FROM pg_indexes WHERE schemaname=current_schema() AND indexname='workflow_versions_history'");
        var definition = Assert.IsType<string>(await indexQuery.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        Assert.Contains("(workflow_id, created_at DESC, id DESC)", definition);
    }

    [Fact]
    public async Task Versions_history_distinguishes_authorized_empty_missing_and_unknown_scope_and_rechecks_revocation()
    {
        var group = Guid.NewGuid();
        var seed = await Seed(RegistryRole.Viewer, group, false);
        var workflow = seed.Type + "/empty";
        await using (var insert = database.DataSource.CreateCommand("INSERT INTO workflows(id,type_id) VALUES($1,$2)"))
        {
            insert.Parameters.AddWithValue(workflow);
            insert.Parameters.AddWithValue(seed.Type);
            await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        await using var server = await Server.Start(database.DataSource);
        var jwt = server.Token(group: group);
        var empty = await VersionPage(server, VersionsPath(workflow), jwt);
        Assert.Empty(empty.Items);
        Assert.Null(empty.ActiveVersionId);
        Assert.Null(empty.LatestVersionId);
        Assert.Null(empty.NextCursor);
        using (var missing = await server.GetPath(VersionsPath(workflow + "-missing"), jwt))
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using (var unknown = await server.GetPath(VersionsPath(seed.Other + "/missing/empty"), jwt))
            await Check(unknown, false);
        var id = await StorePlanVersion(workflow, "1.0.0");
        await Save(new(seed.Team, [new(seed.Team, "release", "description", new([]))]));
        using (var revoked = await server.GetPath(VersionsPath(workflow), jwt)) await Check(revoked, false);
        using (var revokedDetail = await server.GetPath(VersionPath(workflow, id), jwt)) await Check(revokedDetail, false);
        using var admin = await server.GetPath(VersionsPath(workflow), server.Token(identity: "app", scope: null, role: "Admin"));
        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
    }

    [Fact]
    public async Task Versions_history_rejects_bad_query_cursor_transplants_anonymous_and_workload_callers()
    {
        var group = Guid.NewGuid();
        var seed = await Seed(RegistryRole.Viewer, group, false);
        var workflow = seed.Type + "/queries";
        await StorePlanVersion(workflow, "1.0.0");
        await StorePlanVersion(workflow, "1.0.1");
        await using var server = await Server.Start(database.DataSource);
        var jwt = server.Token(group: group);
        var path = VersionsPath(workflow);
        var page = await VersionPage(server, path + "?limit=1", jwt);
        Assert.NotNull(page.NextCursor);
        foreach (var suffix in new[] { "?limit=0", "?limit=101", "?limit=01", "?limit=1&limit=2",
                     "?cursor=", "?cursor=x&cursor=y", "?active=true", "?Limit=1",
                     "?limit=2&cursor=" + Uri.EscapeDataString(page.NextCursor) })
        {
            using var invalid = await server.GetPath(path + suffix, jwt);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.True(invalid.Headers.CacheControl!.NoStore);
        }
        using (var transplanted = await server.GetPath(VersionsPath(workflow + "-other") +
                   "?limit=1&cursor=" + Uri.EscapeDataString(page.NextCursor), jwt))
            Assert.Equal(HttpStatusCode.BadRequest, transplanted.StatusCode);
        using (var anonymous = await server.GetPath(path, null)) Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var workload = await server.GetPath(path, server.Token(group: group, identity: "app", scope: null, role: "Registrar"));
        await Check(workload, false);
    }

    [Fact]
    public async Task Versions_history_storage_failure_is_explicit_and_openapi_exposes_bounded_typed_pages()
    {
        await using (var unavailable = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Username=unused;Database=unused;Timeout=1"))
        await using (var server = await Server.Start(unavailable))
        {
            using var response = await server.GetPath(VersionsPath("team/type/workflow"), server.Token(role: "Admin"));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Contains("version_data_unavailable", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }
        await using var healthy = await Server.Start(database.DataSource);
        using var openapi = await healthy.GetPath("/openapi/v1.json", healthy.Token(role: "Admin"));
        using var document = JsonDocument.Parse(await openapi.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        var operation = document.RootElement.GetProperty("paths")
            .GetProperty("/api/v1/workflows/{team}/{type}/{workflow}/versions").GetProperty("get");
        Assert.Equal("#/components/schemas/WorkflowVersionPage", operation.GetProperty("responses").GetProperty("200")
            .GetProperty("content").GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
        var limit = operation.GetProperty("parameters").EnumerateArray().Single(parameter => parameter.GetProperty("name").GetString() == "limit");
        Assert.Equal(1, limit.GetProperty("schema").GetProperty("minimum").GetInt32());
        Assert.Equal(100, limit.GetProperty("schema").GetProperty("maximum").GetInt32());
        Assert.True(document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty("WorkflowVersionPlan")
            .GetProperty("properties").TryGetProperty("registration", out _));
    }

    private static string VersionsPath(string workflow) =>
        "/api/v1/workflows/" + string.Join('/', workflow.Split('/').Select(Uri.EscapeDataString)) + "/versions";

    private static async Task<WorkflowVersionPage> VersionPage(Server server, string path, string jwt)
    {
        using var response = await server.GetPath(path, jwt);
        Assert.True(response.StatusCode == HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.True(response.Headers.CacheControl!.NoStore);
        return (await response.Content.ReadFromJsonAsync(VersionsJsonContext.Default.WorkflowVersionPage,
            TestContext.Current.CancellationToken))!;
    }

    private async Task SetVersionTimes(string workflow, DateTimeOffset at, Guid? version = null)
    {
        await using var query = database.DataSource.CreateCommand(
            "UPDATE workflow_versions SET created_at=$2 WHERE workflow_id=$1 AND ($3::uuid IS NULL OR id=$3)");
        query.Parameters.AddWithValue(workflow);
        query.Parameters.AddWithValue(at);
        query.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Uuid, (object?)version ?? DBNull.Value);
        await query.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
