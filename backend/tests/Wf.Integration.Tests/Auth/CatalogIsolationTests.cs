using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Wf.Contracts;
using Wf.Contracts.Registry;
using Wf.ControlPlane.Api;
using Wf.Sdk;

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
    public async Task Catalog_filters_teams_types_workflows_and_latest_status_by_actual_caller_grants(RegistryRole role, bool teamScope)
    {
        var group = Guid.NewGuid();
        var seed = await Seed(role, group, teamScope);
        var own = seed.Type + "/workflow";
        var sibling = seed.Team + "/sibling/workflow";
        var other = seed.OtherType + "/workflow";
        await CatalogWorkflow(own);
        await CatalogWorkflow(sibling);
        await CatalogWorkflow(other);
        await CatalogRun(own, RunStatus.Failed, DateTimeOffset.UtcNow.AddMinutes(-1));
        var newest = await CatalogRun(own, RunStatus.Succeeded, DateTimeOffset.UtcNow);
        await using var server = await Server.Start(database.DataSource);
        var jwt = server.Token(group: group);

        using var teamsResponse = await server.GetPath("/api/v1/teams", jwt);
        Assert.Equal(HttpStatusCode.OK, teamsResponse.StatusCode);
        Assert.True(teamsResponse.Headers.CacheControl!.NoStore);
        var teams = await teamsResponse.Content.ReadFromJsonAsync<TeamRegistry[]>(TestContext.Current.CancellationToken);
        var team = Assert.Single(teams!);
        Assert.Equal(seed.Team, team.Team);
        Assert.Equal(teamScope ? new[] { "release", "sibling" } : new[] { "release" }, team.Types.Select(type => type.Name));
        Assert.Empty(team.RoleBindings);
        Assert.All(team.Types, type => Assert.Empty(type.RoleBindings));
        Assert.DoesNotContain(seed.Other, await teamsResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        using var typesResponse = await server.GetPath("/api/v1/teams/" + seed.Team + "/types", jwt);
        var types = await typesResponse.Content.ReadFromJsonAsync<CatalogType[]>(TestContext.Current.CancellationToken);
        Assert.Equal(teamScope ? 2 : 1, types!.Length);
        var release = Assert.Single(types, type => type.Name == "release");
        Assert.Equal(1, release.LatestStatusCounts["Succeeded"]);
        Assert.False(release.LatestStatusCounts.ContainsKey("Failed"));
        using var deniedTypes = await server.GetPath("/api/v1/teams/" + seed.Other + "/types", jwt);
        Assert.Equal(HttpStatusCode.NotFound, deniedTypes.StatusCode);

        using var workflowsResponse = await server.GetPath("/api/v1/workflows?team=" + seed.Team, jwt);
        var page = await workflowsResponse.Content.ReadFromJsonAsync<CatalogWorkflowPage>(TestContext.Current.CancellationToken);
        Assert.Equal(teamScope ? 2 : 1, page!.Items.Count);
        var workflow = Assert.Single(page.Items, value => value.Id == own);
        Assert.Equal("1.0.0", workflow.ActiveVersion!.SemVer);
        Assert.Equal("1.0.0", workflow.LatestVersion.SemVer);
        Assert.Equal(newest, workflow.LatestRun!.Id);
        Assert.Equal("Succeeded", workflow.LatestRun.Status);
        Assert.Equal("release", workflow.Metadata.Type);
        Assert.Null(page.NextCursor);
        using var denied = await server.GetPath("/api/v1/workflows?team=" + seed.Other, jwt);
        Assert.Empty((await denied.Content.ReadFromJsonAsync<CatalogWorkflowPage>(TestContext.Current.CancellationToken))!.Items);
        var json = await workflowsResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        foreach (var forbidden in new[] { seed.Other, "variables", "packageUri", "sourceUri", "roleBindings", "manifest" })
            Assert.DoesNotContain(forbidden, json);
    }

    [Fact]
    public async Task Catalog_paging_is_stable_across_inserts_and_excludes_deleted_workflows()
    {
        var group = Guid.NewGuid();
        var seed = await Seed(RegistryRole.Viewer, group, false);
        foreach (var name in new[] { "a", "c", "e", "deleted" }) await CatalogWorkflow(seed.Type + "/" + name);
        await using (var remove = database.DataSource.CreateCommand("UPDATE workflows SET deleted_at=now() WHERE id=$1"))
        {
            remove.Parameters.AddWithValue(seed.Type + "/deleted");
            await remove.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        await using var server = await Server.Start(database.DataSource);
        var jwt = server.Token(group: group);
        using var firstResponse = await server.GetPath("/api/v1/workflows?team=" + seed.Team + "&type=release&limit=2", jwt);
        var first = (await firstResponse.Content.ReadFromJsonAsync<CatalogWorkflowPage>(TestContext.Current.CancellationToken))!;
        Assert.Equal(["a", "c"], first.Items.Select(item => item.Name));
        Assert.All(first.Items, item => Assert.Null(item.LatestRun));
        Assert.NotNull(first.NextCursor);
        await CatalogWorkflow(seed.Type + "/b");
        using var nextResponse = await server.GetPath("/api/v1/workflows?team=" + seed.Team + "&type=release&limit=2&cursor=" + first.NextCursor, jwt);
        var next = (await nextResponse.Content.ReadFromJsonAsync<CatalogWorkflowPage>(TestContext.Current.CancellationToken))!;
        Assert.Equal("e", Assert.Single(next.Items).Name);
        Assert.Null(next.NextCursor);
        Assert.True(nextResponse.Headers.CacheControl!.NoStore);
    }

    [Fact]
    public async Task Catalog_rechecks_revocation_and_rejects_workload_group_overage_and_anonymous_callers()
    {
        var group = Guid.NewGuid();
        var seed = await Seed(RegistryRole.Viewer, group, false);
        await CatalogWorkflow(seed.Type + "/workflow");
        await using var server = await Server.Start(database.DataSource);
        var jwt = server.Token(group: group);
        foreach (var token in new[]
        {
            server.Token(group: group, identity: "app", role: "Agent"),
            server.Token(group: group, extra: [new("hasgroups", "true")])
        })
        {
            using var denied = await server.GetPath("/api/v1/workflows", token);
            Assert.Equal(HttpStatusCode.OK, denied.StatusCode);
            Assert.Empty((await denied.Content.ReadFromJsonAsync<CatalogWorkflowPage>(TestContext.Current.CancellationToken))!.Items);
        }
        using (var anonymous = await server.GetPath("/api/v1/teams", null))
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        await Save(new(seed.Team, [new(seed.Team, "release", "description", new([])), new(seed.Team, "sibling", "description", new([]))]));
        using var revoked = await server.GetPath("/api/v1/workflows?team=" + seed.Team, jwt);
        Assert.Empty((await revoked.Content.ReadFromJsonAsync<CatalogWorkflowPage>(TestContext.Current.CancellationToken))!.Items);
        using var admin = await server.GetPath("/api/v1/workflows?team=" + seed.Team, server.Token(identity: "app", scope: null, role: "Admin"));
        Assert.Single((await admin.Content.ReadFromJsonAsync<CatalogWorkflowPage>(TestContext.Current.CancellationToken))!.Items);
    }

    [Fact]
    public async Task Catalog_type_only_grants_hide_other_type_presentation_and_all_nonadmin_role_maps()
    {
        var group = Guid.NewGuid();
        var teamGroup = Guid.NewGuid();
        var seed = await Seed(RegistryRole.Viewer, group, false);
        await Save(new(seed.Team,
            [new(seed.Team, "release", "description", new([]), [new(RegistryRole.Viewer, group)]),
             new(seed.Team, "sibling", "description", new([]))],
            RoleBindings: [new(RegistryRole.Viewer, teamGroup)],
            Presentation: new(TypeOrder: ["sibling", "release"])));
        await using var server = await Server.Start(database.DataSource);
        using var typeResponse = await server.GetPath("/api/v1/teams", server.Token(group: group));
        var typeTeam = Assert.Single((await typeResponse.Content.ReadFromJsonAsync<TeamRegistry[]>(TestContext.Current.CancellationToken))!);
        Assert.Equal("release", Assert.Single(typeTeam.Types).Name);
        Assert.Null(typeTeam.Presentation);
        var typeJson = await typeResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        foreach (var hidden in new[] { "sibling", group.ToString(), teamGroup.ToString() }) Assert.DoesNotContain(hidden, typeJson);
        using var teamResponse = await server.GetPath("/api/v1/teams", server.Token(group: teamGroup));
        var team = Assert.Single((await teamResponse.Content.ReadFromJsonAsync<TeamRegistry[]>(TestContext.Current.CancellationToken))!);
        Assert.Equal(["sibling", "release"], team.Presentation!.TypeOrder);
        Assert.Empty(team.RoleBindings);
        Assert.All(team.Types, type => Assert.Empty(type.RoleBindings));
        using var global = await server.GetPath("/api/v1/teams", server.Token(role: "Admin"));
        var registry = Assert.Single((await global.Content.ReadFromJsonAsync<TeamRegistry[]>(TestContext.Current.CancellationToken))!,
            value => value.Team == seed.Team);
        Assert.Equal(teamGroup, Assert.Single(registry.RoleBindings).GroupId);
    }

    [Fact]
    public async Task Catalog_preserves_active_and_latest_versions_and_orders_same_timestamp_runs_by_id()
    {
        var group = Guid.NewGuid();
        var seed = await Seed(RegistryRole.Viewer, group, false);
        var workflow = seed.Type + "/workflow";
        await CatalogWorkflow(workflow);
        var version = Guid.CreateVersion7();
        await using (var publish = database.DataSource.CreateCommand("""
            WITH added AS (
                INSERT INTO workflow_versions(id,workflow_id,semver,manifest,package_uri,package_sha256,package_size,
                    source_uri,source_sha256,source_size,git_commit,git_author,registered_by,sdk_version)
                SELECT $2,workflow_id,'1.1.0',
                    jsonb_set(jsonb_set(manifest,'{version}','"1.1.0"'),'{metadata,values}','{"build":"latest"}'),
                    package_uri,package_sha256,package_size,source_uri,source_sha256,source_size,git_commit,git_author,registered_by,sdk_version
                FROM workflow_versions WHERE workflow_id=$1
                RETURNING id)
            UPDATE workflows SET latest_version_id=(SELECT id FROM added) WHERE id=$1
            """))
        {
            publish.Parameters.AddWithValue(workflow);
            publish.Parameters.AddWithValue(version);
            await publish.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        var at = DateTimeOffset.UtcNow.AddSeconds(1);
        var left = await CatalogRun(workflow, RunStatus.Succeeded, at);
        var right = await CatalogRun(workflow, RunStatus.Failed, at);
        await using var server = await Server.Start(database.DataSource);
        using var response = await server.GetPath("/api/v1/workflows?team=" + seed.Team, server.Token(group: group));
        var item = Assert.Single((await response.Content.ReadFromJsonAsync<CatalogWorkflowPage>(TestContext.Current.CancellationToken))!.Items);
        Assert.Equal("1.0.0", item.ActiveVersion!.SemVer);
        Assert.Equal(new(version, "1.1.0"), item.LatestVersion);
        Assert.Empty(item.Metadata.Values);
        Assert.Equal(left.CompareTo(right) > 0 ? left : right, item.LatestRun!.Id);
        Assert.Equal(left.CompareTo(right) > 0 ? "Succeeded" : "Failed", item.LatestRun.Status);
    }

    [Theory]
    [InlineData("?limit=101")]
    [InlineData("?limit=0")]
    [InlineData("?limit=not-an-integer")]
    [InlineData("?team=a&team=b")]
    [InlineData("?type=release")]
    [InlineData("?team=..")]
    [InlineData("?cursor=invalid")]
    [InlineData("?unsupported=ignored")]
    public async Task Catalog_http_invalid_filters_have_explicit_problem_details(string query)
    {
        await using var server = await Server.Start(database.DataSource);
        using var response = await server.GetPath("/api/v1/workflows" + query, server.Token(role: "Admin"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Catalog_openapi_describes_typed_filters_and_the_actual_response_contract()
    {
        await using var server = await Server.Start(database.DataSource);
        using var response = await server.GetPath("/openapi/v1.json", server.Token(role: "Admin"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var operation = document.RootElement.GetProperty("paths").GetProperty("/api/v1/workflows").GetProperty("get");
        var parameters = operation.GetProperty("parameters").EnumerateArray()
            .ToDictionary(parameter => parameter.GetProperty("name").GetString()!, parameter => parameter);
        Assert.Equal(new[] { "cursor", "limit", "team", "type" }, parameters.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("integer", parameters["limit"].GetProperty("schema").GetProperty("type").GetString());
        Assert.Equal("query", parameters["cursor"].GetProperty("in").GetString());
        Assert.Equal("#/components/schemas/CatalogWorkflowPage", operation.GetProperty("responses").GetProperty("200")
            .GetProperty("content").GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
    }

    private async Task CatalogWorkflow(string id)
    {
        var version = Guid.CreateVersion7();
        var type = id[..id.LastIndexOf('/')];
        await using var connection = await database.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using var batch = new NpgsqlBatch(connection, transaction);
        var workflowInsert = new NpgsqlBatchCommand("INSERT INTO workflows(id,type_id) VALUES($1,$2)");
        workflowInsert.Parameters.AddWithValue(id);
        workflowInsert.Parameters.AddWithValue(type);
        batch.BatchCommands.Add(workflowInsert);
        var versionInsert = new NpgsqlBatchCommand("""
            INSERT INTO workflow_versions(id,workflow_id,semver,manifest,package_uri,package_sha256,package_size,
                source_uri,source_sha256,source_size,git_commit,git_author,registered_by,sdk_version)
            VALUES($1,$2,'1.0.0',$3,'packages/'||repeat('a',64)||'.zip',decode(repeat('a',64),'hex'),1,
                'sources/'||repeat('b',64),decode(repeat('b',64),'hex'),1,repeat('c',40),'catalog','catalog','1.0.0')
            """);
        versionInsert.Parameters.AddWithValue(version);
        versionInsert.Parameters.AddWithValue(id);
        var workflow = Workflow.Define(id, "1.0.0", builder =>
            builder.Type(id.Split('/')[1]).Node("work", node => node.Task("task", _ => { })));
        using var manifest = JsonDocument.Parse(ManifestWriter.WriteUtf8(workflow));
        versionInsert.Parameters.AddWithValue(NpgsqlDbType.Jsonb, manifest.RootElement.GetRawText());
        batch.BatchCommands.Add(versionInsert);
        var pointers = new NpgsqlBatchCommand("UPDATE workflows SET active_version_id=$2,latest_version_id=$2 WHERE id=$1");
        pointers.Parameters.AddWithValue(id);
        pointers.Parameters.AddWithValue(version);
        batch.BatchCommands.Add(pointers);
        await batch.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        await transaction.CommitAsync(TestContext.Current.CancellationToken);
    }

    private async Task<Guid> CatalogRun(string workflow, RunStatus status, DateTimeOffset createdAt)
    {
        var id = Guid.CreateVersion7();
        await using var query = database.DataSource.CreateCommand("""
            INSERT INTO runs(run_day,id,workflow_id,version_id,trigger,triggered_by,variables,metadata,status,created_at)
            VALUES(uuid_v7_run_day($1),$1,$2,$3,0,'catalog','{}','{}',$4,$5)
            """);
        query.Parameters.AddWithValue(id);
        query.Parameters.AddWithValue(workflow);
        query.Parameters.AddWithValue(Guid.CreateVersion7());
        query.Parameters.AddWithValue((short)status);
        query.Parameters.AddWithValue(createdAt);
        await query.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return id;
    }
}
