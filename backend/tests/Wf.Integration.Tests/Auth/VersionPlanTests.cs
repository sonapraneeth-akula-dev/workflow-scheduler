using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Wf.Contracts.Definition;
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
    public async Task Immutable_task_plan_uses_stored_viewer_scope_and_remains_pinned_after_a_new_version(
        RegistryRole role, bool teamScope)
    {
        var group = Guid.NewGuid();
        var seed = await Seed(role, group, teamScope);
        var workflow = seed.Type + "/plan";
        var original = await StorePlanVersion(workflow, "1.0.0");
        var latest = await StorePlanVersion(workflow, "2.0.0");
        var foreign = await StorePlanVersion(seed.OtherType + "/plan", "1.0.0");
        await using var server = await Server.Start(database.DataSource);
        var jwt = server.Token(group: group);
        using var read = await server.GetPath(VersionPath(workflow, original), jwt);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.True(read.Headers.CacheControl!.NoStore);
        var body = await read.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var plan = JsonSerializer.Deserialize(body, VersionsJsonContext.Default.WorkflowVersionPlan)!;
        Assert.Equal(original, plan.VersionId);
        Assert.Equal("1.0.0", plan.Version);
        Assert.Equal(workflow, plan.WorkflowId);
        var node = Assert.Single(plan.Nodes);
        Assert.Equal("work", node.Key);
        Assert.Equal(3, node.Tasks.Length);
        Assert.EndsWith(".DeclaredPlanTask", node.Tasks[0].Action!.Declaration);
        Assert.Equal(TaskActionKind.SdkFunction, node.Tasks[0].Action!.Kind);
        Assert.Equal(new(TaskActionKind.Command, "dotnet"), node.Tasks[1].Action);
        Assert.Equal(new(TaskActionKind.Http, "POST https://example.com/jobs"), node.Tasks[2].Action);
        Assert.Equal(2, node.Tasks[0].Policy.Retry.Retries);
        Assert.Equal(TimeSpan.FromSeconds(12), node.Tasks[0].Policy.AttemptTimeout);
        foreach (var secret in new[] { "plan-default-secret", "plan-argument-secret", "plan-header-secret",
            "plan-body-secret", "plan-query-secret", "\"variables\"", "\"inputs\"", "packageUri", "sourceUri" })
            Assert.DoesNotContain(secret, body, StringComparison.Ordinal);
        using var newer = await server.GetPath(VersionPath(workflow, latest), jwt);
        Assert.Equal("2.0.0", (await newer.Content.ReadFromJsonAsync(VersionsJsonContext.Default.WorkflowVersionPlan,
            TestContext.Current.CancellationToken))!.Version);
        using (var denied = await server.GetPath(VersionPath(seed.OtherType + "/plan", foreign), jwt))
            await Check(denied, false);
        using var wrongWorkflow = await server.GetPath(VersionPath(workflow + "-other", original), jwt);
        Assert.Equal(HttpStatusCode.NotFound, wrongWorkflow.StatusCode);
        using var wrongVersion = await server.GetPath(VersionPath(workflow, foreign), jwt);
        Assert.Equal(HttpStatusCode.NotFound, wrongVersion.StatusCode);
        using var missing = await server.GetPath(VersionPath(workflow, Guid.CreateVersion7()), jwt);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Task_plan_rejects_noncanonical_identity_queries_workloads_and_revoked_access_without_leaking_data()
    {
        var group = Guid.NewGuid();
        var seed = await Seed(RegistryRole.Viewer, group, false);
        var workflow = seed.Type + "/plan";
        var version = await StorePlanVersion(workflow, "1.0.0");
        await using var server = await Server.Start(database.DataSource);
        var path = VersionPath(workflow, version);
        var jwt = server.Token(group: group);
        foreach (var malformed in new[] { path + "?active=true", path.Replace(version.ToString("D"), "invalid", StringComparison.Ordinal),
            path.Replace(version.ToString("D"), Guid.Empty.ToString("D"), StringComparison.Ordinal),
            path.Replace(version.ToString("D"), version.ToString("D").ToUpperInvariant(), StringComparison.Ordinal) })
        {
            using var bad = await server.GetPath(malformed, jwt);
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        }
        using (var anonymous = await server.GetPath(path, null))
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using (var workload = await server.GetPath(path, server.Token(group: group, identity: "app", scope: null, role: "Registrar")))
            await Check(workload, false);
        await Save(new(seed.Team, [new(seed.Team, "release", "description", new([]))]));
        using (var revoked = await server.GetPath(path, jwt)) await Check(revoked, false);
        using var admin = await server.GetPath(path, server.Token(identity: "app", scope: null, role: "Admin"));
        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
    }

    [Fact]
    public async Task Historical_task_plan_survives_workflow_soft_deletion_and_reports_corrupt_identity_without_raw_manifest()
    {
        var group = Guid.NewGuid();
        var seed = await Seed(RegistryRole.Viewer, group, false);
        var workflow = seed.Type + "/plan";
        var version = await StorePlanVersion(workflow, "1.0.0");
        await using var server = await Server.Start(database.DataSource);
        var path = VersionPath(workflow, version);
        var jwt = server.Token(group: group);
        await using (var delete = database.DataSource.CreateCommand("UPDATE workflows SET deleted_at=now() WHERE id=$1"))
        {
            delete.Parameters.AddWithValue(workflow);
            await delete.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        using (var historical = await server.GetPath(path, jwt)) Assert.Equal(HttpStatusCode.OK, historical.StatusCode);
        await using (var corrupt = database.DataSource.CreateCommand(
            "UPDATE workflow_versions SET manifest=jsonb_set(manifest,'{name}','\"corrupt-secret\"'::jsonb) WHERE id=$1"))
        {
            corrupt.Parameters.AddWithValue(version);
            await corrupt.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        using var invalid = await server.GetPath(path, jwt);
        Assert.Equal(HttpStatusCode.InternalServerError, invalid.StatusCode);
        var body = await invalid.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("version_data_inconsistent", body, StringComparison.Ordinal);
        Assert.DoesNotContain("corrupt-secret", body, StringComparison.Ordinal);
        Assert.DoesNotContain("plan-default-secret", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Immutable_task_plan_openapi_describes_the_safe_typed_projection()
    {
        await using var server = await Server.Start(database.DataSource);
        using var response = await server.GetPath("/openapi/v1.json", server.Token(role: "Admin"));
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        var operation = document.RootElement.GetProperty("paths")
            .GetProperty("/api/v1/workflows/{team}/{type}/{workflow}/versions/{version}").GetProperty("get");
        Assert.Equal("#/components/schemas/WorkflowVersionPlan", operation.GetProperty("responses").GetProperty("200")
            .GetProperty("content").GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
        foreach (var status in new[] { "400", "401", "403", "404", "500", "503" })
            Assert.True(operation.GetProperty("responses").TryGetProperty(status, out _));
    }

    private static string VersionPath(string workflow, Guid version) =>
        "/api/v1/workflows/" + string.Join('/', workflow.Split('/').Select(Uri.EscapeDataString)) + "/versions/" + version.ToString("D");

    private async Task<Guid> StorePlanVersion(string id, string semver)
    {
        var version = Guid.CreateVersion7();
        var workflow = Workflow.Define(id, semver, builder => builder.Type(id.Split('/')[1])
            .Variables(variables => variables.String("private", "plan-default-secret"))
            .Node("work", node => node.Task("sdk", DeclaredPlanTask, policy => policy.Timeout(TimeSpan.FromSeconds(12)).Retry(2))
                .Task("command", Tasks.Command("dotnet", "--token plan-argument-secret"))
                .Task("http", Tasks.Http(HttpMethod.Post, "https://example.com/jobs?token=plan-query-secret",
                    headers: new Dictionary<string, string> { ["X-Token"] = "plan-header-secret" }, body: "plan-body-secret"))));
        await using var connection = await database.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using var batch = new NpgsqlBatch(connection, transaction);
        var create = new NpgsqlBatchCommand("INSERT INTO workflows(id,type_id) VALUES($1,$2) ON CONFLICT(id) DO NOTHING");
        create.Parameters.AddWithValue(id);
        create.Parameters.AddWithValue(id[..id.LastIndexOf('/')]);
        batch.BatchCommands.Add(create);
        var publish = new NpgsqlBatchCommand("""
            INSERT INTO workflow_versions(id,workflow_id,semver,manifest,package_uri,package_sha256,package_size,
                source_uri,source_sha256,source_size,git_commit,git_author,registered_by,sdk_version)
            VALUES($1,$2,$3,$4,'packages/'||repeat('a',64)||'.zip',decode(repeat('a',64),'hex'),1,
                'sources/'||repeat('b',64),decode(repeat('b',64),'hex'),1,repeat('c',40),'plan','plan','1.0.0')
            """);
        publish.Parameters.AddWithValue(version);
        publish.Parameters.AddWithValue(id);
        publish.Parameters.AddWithValue(semver);
        publish.Parameters.AddWithValue(NpgsqlDbType.Jsonb, ManifestWriter.Write(workflow));
        batch.BatchCommands.Add(publish);
        var activate = new NpgsqlBatchCommand("UPDATE workflows SET active_version_id=$2,latest_version_id=$2 WHERE id=$1");
        activate.Parameters.AddWithValue(id);
        activate.Parameters.AddWithValue(version);
        batch.BatchCommands.Add(activate);
        await batch.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        await transaction.CommitAsync(TestContext.Current.CancellationToken);
        return version;
    }

    private static Task DeclaredPlanTask(TaskContext _) => throw new InvalidOperationException("Task Plan must not execute.");
}
