using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Wf.ControlPlane.Api.Admin;
using Wf.ControlPlane.Data;
using Wf.ControlPlane.Registry;
using Wf.Integration.Tests.Execution;
using Wf.Integration.Tests.Registration;

namespace Wf.Integration.Tests;

public sealed class RetentionEndpointTests(RegistrationHostDatabase database) : IClassFixture<RegistrationHostDatabase>
{
    private const string Path = "/api/v1/admin/retention";
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private static RetentionPolicy Policy(int days = 90, int audit = 365) => new(days, audit, new());

    [Fact]
    public async Task Default_is_ninety_days_audit_is_one_year_and_full_replacement_is_durable_and_audited()
    {
        await Reset();
        var type = await Type();
        await using var api = await MetricsApiServer.Start(database.DataSource, Token);
        var jwt = api.Jwt(role: "Admin");
        var initial = await Get(api, jwt);
        Assert.Equal((90, 365, 0), (initial.DefaultDays, initial.AuditDays, initial.PerType.Count));
        var policy = new RetentionPolicy(120, 730, new() { [type] = 30 });
        using (var updated = await Put(api, jwt, policy))
        {
            Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
            Equal(policy, await Read(updated));
        }
        Equal(policy, await Get(api, jwt));
        await using (var second = await MetricsApiServer.Start(database.DataSource, Token))
            Equal(policy, await Get(second, second.Jwt(role: "Admin")));
        var actor = new JwtSecurityTokenHandler().ReadJwtToken(jwt).Claims.Single(c => c.Type == "oid").Value;
        await using (var audit = database.DataSource.CreateCommand("""
            SELECT actor_kind,summary::text FROM audit_log
            WHERE actor=$1 AND action='retention.update' AND target_kind='Retention' AND target_id='global'
            """))
        {
            audit.Parameters.AddWithValue(actor);
            await using var reader = await audit.ExecuteReaderAsync(Token);
            Assert.True(await reader.ReadAsync(Token));
            Assert.Equal("User", reader.GetString(0));
            using var summary = JsonDocument.Parse(reader.GetString(1));
            Equal(initial, summary.RootElement.GetProperty("before").Deserialize(RetentionJson.Default.RetentionPolicy)!);
            Equal(policy, summary.RootElement.GetProperty("after").Deserialize(RetentionJson.Default.RetentionPolicy)!);
            Assert.False(await reader.ReadAsync(Token));
        }
        using (var replacement = await Put(api, jwt, Policy(60)))
            Assert.Equal(HttpStatusCode.OK, replacement.StatusCode);
        Equal(Policy(60), await Get(api, jwt));
        await using var stored = database.DataSource.CreateCommand("SELECT retention_days IS NULL FROM workflow_types WHERE id=$1");
        stored.Parameters.AddWithValue(type);
        Assert.Equal(true, await stored.ExecuteScalarAsync(Token));
    }

    [Theory]
    [InlineData(null, 401)]
    [InlineData("", 403)]
    [InlineData("Operator", 403)]
    [InlineData("Viewer", 403)]
    [InlineData("Registrar", 403)]
    [InlineData("Agent", 403)]
    public async Task Only_existing_global_Admin_authorization_can_read_or_change_retention(string? role, int status)
    {
        await using var api = await MetricsApiServer.Start(database.DataSource, Token);
        var jwt = role is null ? null : api.Jwt(role: role);
        using var get = await api.Get(Path, jwt, Token);
        using var put = await Put(api, jwt, Policy());
        Assert.Equal(status, (int)get.StatusCode);
        Assert.Equal(status, (int)put.StatusCode);
    }

    [Fact]
    public async Task Existing_app_Admin_can_configure_retention_and_audit_records_App_actor_kind()
    {
        await using var api = await MetricsApiServer.Start(database.DataSource, Token);
        var jwt = api.Jwt(role: "Admin", identity: "app");
        using var response = await Put(api, jwt, Policy());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var actor = new JwtSecurityTokenHandler().ReadJwtToken(jwt).Claims.Single(c => c.Type == "oid").Value;
        await using var query = database.DataSource.CreateCommand("SELECT actor_kind FROM audit_log WHERE actor=$1 AND action='retention.update'");
        query.Parameters.AddWithValue(actor);
        Assert.Equal("App", await query.ExecuteScalarAsync(Token));
    }

    [Theory]
    [InlineData("")]
    [InlineData("invalid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Admin_without_valid_audit_identity_cannot_read_or_write_configuration(string actor)
    {
        await using var api = await MetricsApiServer.Start(database.DataSource, Token);
        var jwt = api.Jwt(role: "Admin", actor: actor);
        using var get = await api.Get(Path, jwt, Token);
        using var put = await Put(api, jwt, Policy());
        await Problem(get, 403);
        await Problem(put, 403);
    }

    [Theory]
    [InlineData("""{"default_days":90,"audit_days":30,"per_type":{}}""")]
    [InlineData("""{"default_days":0,"audit_days":365,"per_type":{}}""")]
    [InlineData("""{"default_days":90,"audit_days":365,"per_type":{"missing/type":10}}""")]
    [InlineData("""{"default_days":90,"audit_days":365,"per_type":{"team/..":10}}""")]
    [InlineData("""{"default_days":90,"audit_days":365,"per_type":null}""")]
    [InlineData("""{"default_days":90,"audit_days":365,"per_type":{},"extra":1}""")]
    [InlineData("""{"default_days":90,"default_days":30,"audit_days":365,"per_type":{}}""")]
    [InlineData("""{"default_days":90,"audit_days":365,"per_type":{"team/type":30,"team/type":60}}""")]
    [InlineData("""{"default_days":90,"per_type":{}}""")]
    [InlineData("null")]
    [InlineData("{")]
    public async Task Invalid_requests_including_thirty_day_audit_are_400_without_mutation_or_audit(string json)
    {
        await Reset();
        await using var api = await MetricsApiServer.Start(database.DataSource, Token);
        var jwt = api.Jwt(role: "Admin");
        var before = await AuditCount();
        using var response = await RawPut(api, jwt, json);
        await Problem(response, 400);
        Equal(Policy(), await Get(api, jwt));
        Assert.Equal(before, await AuditCount());
    }

    [Fact]
    public async Task Bounded_content_type_body_and_query_errors_are_explicit()
    {
        await using var api = await MetricsApiServer.Start(database.DataSource, Token);
        var jwt = api.Jwt(role: "Admin");
        using (var unsupported = await RawPut(api, jwt, "{}", "text/plain")) await Problem(unsupported, 415);
        using (var huge = await RawPut(api, jwt, new string(' ', 65537))) await Problem(huge, 413);
        using (var chunked = new StringContent(new string(' ', 65537), Encoding.UTF8, "application/json"))
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, api.Api + Path) { Content = chunked };
            request.Headers.Authorization = new("Bearer", jwt);
            request.Headers.TransferEncodingChunked = true;
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false });
            using var response = await client.SendAsync(request, Token);
            await Problem(response, 413);
        }
        using (var get = await api.Get(Path + "?unknown=1", jwt, Token)) await Problem(get, 400);
        using var put = await api.Send(HttpMethod.Put, Path + "?unknown=1", jwt,
            new StringContent(JsonSerializer.Serialize(Policy(), RetentionJson.Default.RetentionPolicy), Encoding.UTF8, "application/json"), Token);
        await Problem(put, 400);
    }

    [Fact]
    public async Task Audit_failure_rolls_back_defaults_overrides_and_configuration_then_retry_succeeds()
    {
        await Reset();
        var type = await Type();
        var policy = new RetentionPolicy(120, 730, new() { [type] = 15 });
        await using var api = await MetricsApiServer.Start(database.DataSource, Token);
        var jwt = api.Jwt(role: "Admin");
        var name = "reject_retention_" + Guid.NewGuid().ToString("N");
        await Execute($"""
            CREATE FUNCTION {name}() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'injected retention audit failure'; END $$;
            CREATE TRIGGER {name} BEFORE INSERT ON audit_log FOR EACH ROW
                WHEN (NEW.action='retention.update') EXECUTE FUNCTION {name}();
            """);
        var before = await AuditCount();
        try
        {
            using var failed = await Put(api, jwt, policy);
            await Problem(failed, 503);
            Assert.DoesNotContain("injected", await failed.Content.ReadAsStringAsync(Token));
            Equal(Policy(), await Get(api, jwt));
            Assert.Equal(before, await AuditCount());
        }
        finally { await Execute($"DROP TRIGGER {name} ON audit_log; DROP FUNCTION {name}();"); }
        using var retried = await Put(api, jwt, policy);
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        Equal(policy, await Get(api, jwt));
    }

    [Fact]
    public async Task Twelve_concurrent_replacements_have_no_torn_reads_and_form_one_audit_chain()
    {
        await Reset();
        var type = await Type();
        await using var api = await MetricsApiServer.Start(database.DataSource, Token);
        var jwt = api.Jwt(role: "Admin");
        var before = await AuditCount();
        var writes = Enumerable.Range(1, 12).Select(async index =>
        {
            using var response = await Put(api, jwt, new(90 + index, 365 + index, new() { [type] = index }));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var receipt = await Read(response);
            Assert.Equal((90 + index, 365 + index, index), (receipt.DefaultDays, receipt.AuditDays, receipt.PerType[type]));
        }).ToArray();
        for (var index = 0; index < 24; index++)
        {
            var read = await Get(api, jwt);
            if (read.PerType.Count == 0) Equal(Policy(), read);
            else Assert.Equal((90 + read.PerType[type], 365 + read.PerType[type]), (read.DefaultDays, read.AuditDays));
        }
        await Task.WhenAll(writes);
        Assert.Equal(before + 12, await AuditCount());
        var previous = Policy();
        await using var query = database.DataSource.CreateCommand(
            "SELECT summary::text FROM audit_log WHERE action='retention.update' AND actor=$1 ORDER BY id");
        query.Parameters.AddWithValue(new JwtSecurityTokenHandler().ReadJwtToken(jwt).Claims.Single(c => c.Type == "oid").Value);
        await using (var reader = await query.ExecuteReaderAsync(Token))
            while (await reader.ReadAsync(Token))
            {
                using var summary = JsonDocument.Parse(reader.GetString(0));
                Equal(previous, summary.RootElement.GetProperty("before").Deserialize(RetentionJson.Default.RetentionPolicy)!);
                previous = summary.RootElement.GetProperty("after").Deserialize(RetentionJson.Default.RetentionPolicy)!;
            }
        Equal(previous, await Get(api, jwt));
    }

    [Fact]
    public async Task Missing_singleton_is_not_success_shaped_default_and_storage_errors_are_sanitized()
    {
        await using var api = await MetricsApiServer.Start(database.DataSource, Token);
        var jwt = api.Jwt(role: "Admin");
        await Execute("DELETE FROM retention_config");
        try
        {
            using (var get = await api.Get(Path, jwt, Token)) await Problem(get, 500);
            using var put = await Put(api, jwt, Policy());
            await Problem(put, 500);
        }
        finally { await Execute("INSERT INTO retention_config(singleton) VALUES(true)"); }
        await Execute("ALTER TABLE retention_config RENAME COLUMN default_days TO unavailable_retention_days");
        try
        {
            using var failed = await api.Get(Path, jwt, Token);
            await Problem(failed, 503);
            Assert.DoesNotContain("unavailable_retention_days", await failed.Content.ReadAsStringAsync(Token));
        }
        finally { await Execute("ALTER TABLE retention_config RENAME COLUMN unavailable_retention_days TO default_days"); }
        await Get(api, jwt);
    }

    [Theory]
    [InlineData("default_days=0")]
    [InlineData("default_days=36501")]
    [InlineData("audit_days=30")]
    [InlineData("audit_days=36501")]
    public async Task Database_guards_do_not_allow_short_audit_or_invalid_default(string mutation)
    {
        await using var command = database.DataSource.CreateCommand("UPDATE retention_config SET " + mutation);
        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(Token));
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
    }

    [Fact]
    public async Task One_connection_pool_can_read_replace_and_audit_without_nested_connection_waits()
    {
        var type = await Type();
        await using var source = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(database.ConnectionString) { MaxPoolSize = 1 }.ConnectionString);
        await using var api = await MetricsApiServer.Start(source, Token);
        var jwt = api.Jwt(role: "Admin");
        var policy = new RetentionPolicy(100, 400, new() { [type] = 20 });
        using var response = await Put(api, jwt, policy);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Equal(policy, await Get(api, jwt));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Migrator_fresh_and_previous_schema_upgrade_preserves_overrides_and_is_repeatable(bool previousSchema)
    {
        var schema = "retention_" + Guid.NewGuid().ToString("N");
        await Execute($"CREATE SCHEMA {schema}");
        var connection = new NpgsqlConnectionStringBuilder(database.ConnectionString) { SearchPath = schema }.ConnectionString;
        try
        {
            var scripts = Migrator.LoadScripts();
            if (previousSchema)
            {
                Migrator.Upgrade(connection, NullLogger.Instance, scripts.Where(s => string.CompareOrdinal(s.Name, "0040") < 0));
                await using var old = NpgsqlDataSource.Create(connection);
                await using var seed = old.CreateCommand("""
                    INSERT INTO teams(id,name) VALUES('retention','Retention');
                    INSERT INTO workflow_types(id,team_id,name,normalized_name,description,metadata_schema,retention_days)
                    VALUES('retention/type','retention','type','type','Retained override','{}',14)
                    """);
                await seed.ExecuteNonQueryAsync(Token);
            }
            var applied = Migrator.Upgrade(connection, NullLogger.Instance);
            Assert.Contains("0040_retention_config.sql", applied);
            Assert.Empty(Migrator.Upgrade(connection, NullLogger.Instance));
            await using var fresh = NpgsqlDataSource.Create(connection);
            await using var api = await MetricsApiServer.Start(fresh, Token);
            var policy = await Get(api, api.Jwt(role: "Admin"));
            Assert.Equal((90, 365), (policy.DefaultDays, policy.AuditDays));
            if (previousSchema) Assert.Equal(14, Assert.Single(policy.PerType).Value);
            else Assert.Empty(policy.PerType);
        }
        finally { await Execute($"DROP SCHEMA {schema} CASCADE"); }
    }

    private Task Reset() => Execute("UPDATE retention_config SET default_days=90,audit_days=365; UPDATE workflow_types SET retention_days=NULL");
    private async Task<string> Type()
    {
        var team = "retention-" + Guid.NewGuid().ToString("N");
        await using var connection = await database.DataSource.OpenConnectionAsync(Token);
        await using var transaction = await connection.BeginTransactionAsync(Token);
        await new RegistryWriter().UpsertAsync(connection, transaction, new(team,
            [new(team, "type", "Retention integration", new([]))]), Token);
        await transaction.CommitAsync(Token);
        return team + "/type";
    }
    private async Task Execute(string sql)
    {
        await using var command = database.DataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(Token);
    }
    private async Task<long> AuditCount()
    {
        await using var command = database.DataSource.CreateCommand("SELECT count(*) FROM audit_log WHERE action='retention.update'");
        return (long)(await command.ExecuteScalarAsync(Token))!;
    }
    private Task<HttpResponseMessage> Put(MetricsApiServer api, string? jwt, RetentionPolicy policy) =>
        RawPut(api, jwt, JsonSerializer.Serialize(policy, RetentionJson.Default.RetentionPolicy));
    private Task<HttpResponseMessage> RawPut(MetricsApiServer api, string? jwt, string json, string contentType = "application/json") =>
        api.Send(HttpMethod.Put, Path, jwt, new StringContent(json, Encoding.UTF8, contentType), Token);
    private async Task<RetentionPolicy> Get(MetricsApiServer api, string jwt)
    {
        using var response = await api.Get(Path, jwt, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        return await Read(response);
    }
    private async Task<RetentionPolicy> Read(HttpResponseMessage response) =>
        JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(Token), RetentionJson.Default.RetentionPolicy)!;
    private static void Equal(RetentionPolicy expected, RetentionPolicy actual)
    {
        Assert.Equal((expected.DefaultDays, expected.AuditDays), (actual.DefaultDays, actual.AuditDays));
        Assert.Equal(expected.PerType.OrderBy(p => p.Key), actual.PerType.OrderBy(p => p.Key));
    }
    private async Task Problem(HttpResponseMessage response, int status)
    {
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        Assert.Equal(status, json.RootElement.GetProperty("status").GetInt32());
    }
}
