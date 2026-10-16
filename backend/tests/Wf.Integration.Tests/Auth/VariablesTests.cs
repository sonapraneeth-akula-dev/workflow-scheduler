using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Wf.Contracts.Definition;
using Wf.Contracts.Manifest;
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
    public async Task Variable_schema_requires_fresh_operator_or_admin_before_reading_defaults(RegistryRole role, bool teamScope)
    {
        var group = Guid.NewGuid();
        var seed = await Seed(role, group, teamScope);
        var workflow = seed.Type + "/variables";
        var version = await StorePlanVersion(workflow, "1.0.0");
        await using var server = await Server.Start(database.DataSource);
        var jwt = server.Token(group: group);
        foreach (var path in new[] { VariablesPath(workflow), VariablesPath(workflow, version) })
        {
            using var response = await server.GetPath(path, jwt);
            var allowed = role is RegistryRole.Operator or RegistryRole.Admin;
            Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.Forbidden, response.StatusCode);
            Assert.True(response.Headers.CacheControl!.NoStore);
            Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
            if (!allowed)
                Assert.DoesNotContain("plan-default-secret", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }
        using (var other = await server.GetPath(VariablesPath(seed.OtherType + "/absent"), jwt))
            Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
        if (role is not (RegistryRole.Operator or RegistryRole.Admin))
        {
            await SetVariableSchemas(version, "[null]");
            using var deniedCorrupt = await server.GetPath(VariablesPath(workflow, version), jwt);
            Assert.Equal(HttpStatusCode.Forbidden, deniedCorrupt.StatusCode);
            await SetVariableSchemas(version, "[]");
        }
        await Save(new(seed.Team, [new(seed.Team, "release", "description", new([]))]));
        using var revoked = await server.GetPath(VariablesPath(workflow, version), jwt);
        Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
        using var global = await server.GetPath(VariablesPath(workflow, version), server.Token(identity: "app", scope: null, role: "Admin"));
        Assert.Equal(HttpStatusCode.OK, global.StatusCode);
    }

    [Fact]
    public async Task Variable_schema_projects_only_all_nine_typed_defaults_and_remains_pinned_without_cache()
    {
        var seed = await Seed(RegistryRole.Operator, Guid.NewGuid(), false);
        var workflow = seed.Type + "/variables";
        var first = await StorePlanVersion(workflow, "1.0.0");
        const string schemas = """
            [{"key":"string","type":"String","required":true,"default":"literal","allowedValues":["literal","other"]},
             {"key":"int","type":"Int","default":2147483647},
             {"key":"long","type":"Long","default":9223372036854775807},
             {"key":"double","type":"Double","default":1.5},
             {"key":"bool","type":"Bool","default":false},
             {"key":"enum","type":"Enum","default":"Prod","allowedValues":["Dev","Prod"]},
             {"key":"list","type":"StringList","default":["a","b"]},
             {"key":"duration","type":"Duration","default":"1.02:03:04"},
             {"key":"secret","type":"Secret","default":{"Name":"deploy-token"}},
             {"key":"optional","type":"String"}]
            """;
        await SetVariableSchemas(first, schemas);
        await using var server = await Server.Start(database.DataSource);
        var jwt = server.Token(role: "Admin");
        using var original = await server.GetPath(VariablesPath(workflow), jwt);
        Assert.Equal(HttpStatusCode.OK, original.StatusCode);
        var body = await original.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        Assert.Equal(["workflowId", "versionId", "version", "variables"], root.EnumerateObject().Select(p => p.Name));
        Assert.Equal(first, root.GetProperty("versionId").GetGuid());
        Assert.Equal(workflow, root.GetProperty("workflowId").GetString());
        Assert.Equal("1.0.0", root.GetProperty("version").GetString());
        var variables = root.GetProperty("variables").EnumerateArray().ToArray();
        Assert.Equal(10, variables.Length);
        using var expected = JsonDocument.Parse(schemas);
        for (var index = 0; index < variables.Length; index++)
        {
            var value = variables[index];
            var schema = expected.RootElement[index];
            Assert.Equal(["key", "type", "required", "default", "allowedValues"], value.EnumerateObject().Select(p => p.Name));
            Assert.Equal(schema.GetProperty("type").GetString(), value.GetProperty("type").GetString());
            Assert.Equal(schema.GetProperty("key").GetString(), value.GetProperty("key").GetString());
            Assert.Equal(index == 0, value.GetProperty("required").GetBoolean());
            if (schema.TryGetProperty("default", out var literal))
                Assert.True(JsonElement.DeepEquals(literal, value.GetProperty("default")));
            else Assert.Equal(JsonValueKind.Null, value.GetProperty("default").ValueKind);
            if (schema.TryGetProperty("allowedValues", out var allowed))
                Assert.True(JsonElement.DeepEquals(allowed, value.GetProperty("allowedValues")));
            else Assert.Empty(value.GetProperty("allowedValues").EnumerateArray());
        }
        Assert.Equal(long.MaxValue, variables[2].GetProperty("default").GetInt64());
        foreach (var privateData in new[] { "plan-argument-secret", "plan-header-secret", "plan-body-secret",
            "plan-query-secret", "packageUri", "sourceUri", "\"nodes\"", "\"inputs\"", "\"outputs\"", "\"env\"" })
            Assert.DoesNotContain(privateData, body, StringComparison.Ordinal);
        var latest = await StorePlanVersion(workflow, "2.0.0");
        using var pinned = await server.GetPath(VariablesPath(workflow, first), jwt);
        Assert.Equal(body, await pinned.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        using var active = await server.GetPath(VariablesPath(workflow), jwt);
        var selected = await active.Content.ReadFromJsonAsync(VariablesJsonContext.Default.WorkflowVariablesResponse,
            TestContext.Current.CancellationToken);
        Assert.Equal(latest, selected!.VersionId);
        Assert.Equal("2.0.0", selected.Version);
    }

    [Fact]
    public async Task Variable_schema_rejects_unknown_duplicate_and_noncanonical_queries_and_identities()
    {
        var seed = await Seed(RegistryRole.Operator, Guid.NewGuid(), false);
        var workflow = seed.Type + "/variables";
        var version = await StorePlanVersion(workflow, "1.0.0");
        await using var server = await Server.Start(database.DataSource);
        var path = VariablesPath(workflow);
        var jwt = server.Token(role: "Admin");
        foreach (var query in new[] { "?unknown=true", "?version=", "?version", "?Version=" + version,
            "?version=" + version + "&version=" + version, "?version=" + version + "&unknown=true",
            "?version=1.0.0", "?version=" + Guid.Empty, "?version=" + version.ToString("N"),
            "?version=" + version.ToString("D").ToUpperInvariant(), "?version=%20" + version })
        {
            using var invalid = await server.GetPath(path + query, jwt);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        foreach (var name in new[] { "%20", "bad%5Cname", "bad%01name", new string('a', 129) })
        {
            using var invalid = await server.GetPath(VariablesPath(seed.Type + "/" + name), jwt);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        using var anonymous = await server.GetPath(path, null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var workload = await server.GetPath(path, server.Token(identity: "app", scope: null, role: "Registrar"));
        Assert.Equal(HttpStatusCode.Forbidden, workload.StatusCode);
    }

    [Fact]
    public async Task Variable_schema_never_selects_foreign_missing_deleted_or_inactive_versions()
    {
        var seed = await Seed(RegistryRole.Operator, Guid.NewGuid(), false);
        var workflow = seed.Type + "/variables";
        var version = await StorePlanVersion(workflow, "1.0.0");
        var foreign = await StorePlanVersion(seed.Type + "/other", "1.0.0");
        await using var server = await Server.Start(database.DataSource);
        var jwt = server.Token(role: "Admin");
        foreach (var path in new[] { VariablesPath(workflow, foreign), VariablesPath(workflow, Guid.NewGuid()),
            VariablesPath(workflow + "-missing"), VariablesPath(workflow + "-missing", version) })
        {
            using var missing = await server.GetPath(path, jwt);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
        await using (var deactivate = database.DataSource.CreateCommand("UPDATE workflows SET active_version_id=NULL WHERE id=$1"))
        {
            deactivate.Parameters.AddWithValue(workflow);
            await deactivate.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        using (var inactive = await server.GetPath(VariablesPath(workflow), jwt))
            Assert.Equal(HttpStatusCode.NotFound, inactive.StatusCode);
        using (var historical = await server.GetPath(VariablesPath(workflow, version), jwt))
            Assert.Equal(HttpStatusCode.OK, historical.StatusCode);
        await using (var delete = database.DataSource.CreateCommand("UPDATE workflows SET deleted_at=now() WHERE id=$1"))
        {
            delete.Parameters.AddWithValue(workflow);
            await delete.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        foreach (var path in new[] { VariablesPath(workflow), VariablesPath(workflow, version) })
        {
            using var deleted = await server.GetPath(path, jwt);
            Assert.Equal(HttpStatusCode.NotFound, deleted.StatusCode);
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[null]")]
    [InlineData("[{\"key\":\"x\",\"type\":\"Unknown\",\"default\":\"credential-do-not-echo\"}]")]
    [InlineData("[{\"key\":\"x\"}]")]
    [InlineData("[{\"key\":\"x\",\"type\":\"Int\",\"default\":\"credential-do-not-echo\"}]")]
    [InlineData("[{\"key\":\"x\",\"type\":\"Long\",\"default\":9223372036854775808}]")]
    [InlineData("[{\"key\":\"x\",\"type\":\"Secret\",\"default\":{\"Name\":\"token\",\"Value\":\"credential-do-not-echo\"}}]")]
    [InlineData("[{\"key\":\"x\",\"type\":\"String\"},{\"key\":\"x\",\"type\":\"String\"}]")]
    [InlineData("[{\"key\":\"x\",\"type\":\"Enum\",\"allowedValues\":[]}]")]
    [InlineData("[{\"key\":\"x\",\"type\":\"Enum\",\"allowedValues\":[\"a\",\"a\"]}]")]
    [InlineData("[{\"key\":\"x\",\"type\":\"Enum\",\"allowedValues\":[\"a\"],\"default\":\"b\"}]")]
    public async Task Variable_schema_corruption_fails_closed_without_credential_echo(string schemas)
    {
        var seed = await Seed(RegistryRole.Operator, Guid.NewGuid(), false);
        var workflow = seed.Type + "/variables";
        var version = await StorePlanVersion(workflow, "1.0.0");
        await SetVariableSchemas(version, schemas);
        await using var server = await Server.Start(database.DataSource);
        using var response = await server.GetPath(VariablesPath(workflow, version), server.Token(role: "Admin"));
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("version_data_inconsistent", body, StringComparison.Ordinal);
        Assert.DoesNotContain("credential-do-not-echo", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("manifest-'variables'")]
    [InlineData("jsonb_set(manifest,'{name}','\"credential-do-not-echo\"')")]
    [InlineData("jsonb_set(manifest,'{version}','\"9.9.9\"')")]
    public async Task Variable_schema_requires_present_schema_and_matching_stored_manifest_identity(string expression)
    {
        var seed = await Seed(RegistryRole.Operator, Guid.NewGuid(), false);
        var workflow = seed.Type + "/variables";
        var version = await StorePlanVersion(workflow, "1.0.0");
        await using (var corrupt = database.DataSource.CreateCommand($"UPDATE workflow_versions SET manifest={expression} WHERE id=$1"))
        {
            corrupt.Parameters.AddWithValue(version);
            await corrupt.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        await using var server = await Server.Start(database.DataSource);
        using var response = await server.GetPath(VariablesPath(workflow), server.Token(role: "Admin"));
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("credential-do-not-echo", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Variable_schema_openapi_exposes_typed_projection_string_enum_and_optional_version_only()
    {
        await using var server = await Server.Start(database.DataSource);
        using var response = await server.GetPath("/openapi/v1.json", server.Token(role: "Admin"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var operation = document.RootElement.GetProperty("paths")
            .GetProperty("/api/v1/workflows/{team}/{type}/{workflow}/variables").GetProperty("get");
        Assert.Equal("#/components/schemas/WorkflowVariablesResponse", operation.GetProperty("responses").GetProperty("200")
            .GetProperty("content").GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
        var query = Assert.Single(operation.GetProperty("parameters").EnumerateArray(), p => p.GetProperty("in").GetString() == "query");
        Assert.Equal("version", query.GetProperty("name").GetString());
        Assert.False(query.TryGetProperty("required", out var required) && required.GetBoolean());
        var components = document.RootElement.GetProperty("components").GetProperty("schemas");
        var fields = components.GetProperty("WorkflowVariable").GetProperty("properties");
        var type = fields.GetProperty("type");
        if (type.TryGetProperty("$ref", out var reference))
            type = components.GetProperty(reference.GetString()!.Split('/')[^1]);
        Assert.True(type.TryGetProperty("type", out var kind), type.GetRawText());
        Assert.Equal("string", kind.GetString());
        Assert.Equal(Enum.GetNames<VariableType>(), type.GetProperty("enum").EnumerateArray().Select(v => v.GetString()));
        Assert.Equal(["key", "type", "required", "default", "allowedValues"], fields.EnumerateObject().Select(p => p.Name));
        foreach (var status in new[] { "400", "401", "403", "404", "500", "503" })
            Assert.True(operation.GetProperty("responses").TryGetProperty(status, out _));
    }

    [Fact]
    public async Task Variable_schema_reports_storage_unavailable_without_success_fallback_or_credentials()
    {
        await using var source = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(database.DataSource.ConnectionString)
        {
            SearchPath = "missing_schema_" + Guid.NewGuid().ToString("N")
        }.ConnectionString);
        await using var server = await Server.Start(source);
        using var response = await server.GetPath(VariablesPath("team/type/workflow"), server.Token(role: "Admin"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("version_data_unavailable", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Host=", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("missing_schema_", body, StringComparison.Ordinal);
    }

    private static string VariablesPath(string workflow, Guid? version = null) =>
        "/api/v1/workflows/" + workflow + "/variables" + (version.HasValue ? "?version=" + version.Value.ToString("D") : "");

    private async Task SetVariableSchemas(Guid version, string schemas)
    {
        await using var update = database.DataSource.CreateCommand("UPDATE workflow_versions SET manifest=jsonb_set(manifest,'{variables}',$2::jsonb) WHERE id=$1");
        update.Parameters.AddWithValue(version);
        update.Parameters.AddWithValue(NpgsqlDbType.Jsonb, schemas);
        await update.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
