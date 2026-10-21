using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Text;
using System.Text.Json;
using Wf.ControlPlane.Api.Admin;
using Wf.Integration.Tests.Execution;
using Wf.Integration.Tests.Registration;

namespace Wf.Integration.Tests;

public sealed class SystemNoticeEndpointTests(RegistrationHostDatabase database) : IClassFixture<RegistrationHostDatabase>
{
    private const string ReadPath = "/api/v1/system-notice";
    private const string WritePath = "/api/v1/admin/system-notice";
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private static SystemNotice Notice(string message = "Maintenance", DateTimeOffset? expires = null) => new(message, "Warning", expires);

    [Fact]
    public async Task Empty_initial_state_publish_cross_replica_read_expire_and_clear_are_durable_and_audited()
    {
        await Reset();
        var clock = new Clock();
        await using var api = await MetricsApiServer.Start(database.DataSource, Token, timeProvider: clock);
        var jwt = api.Jwt(role: "Admin");
        using (var initial = await api.Get(ReadPath, jwt, Token))
        {
            var state = await Read(initial);
            Assert.Equal(new("0", null, true), state);
            Assert.Equal("\"0\"", initial.Headers.ETag!.Tag);
            Assert.True(initial.Headers.CacheControl!.NoStore);
        }
        var notice = Notice("<script>plain text</script>\nMaintenance", clock.GetUtcNow().AddMinutes(1));
        using (var published = await Send(api, jwt, notice, "\"0\""))
            Assert.Equal(new("1", notice, true), await Read(published));
        await using (var replica = await MetricsApiServer.Start(database.DataSource, Token, timeProvider: clock))
        {
            using var read = await replica.Get(ReadPath, replica.Jwt(role: "Viewer"), Token);
            Assert.Equal(new("1", notice, false), await Read(read));
            Assert.Equal("\"1\"", read.Headers.ETag!.Tag);
        }
        clock.Now = notice.ExpiresAt!.Value;
        using (var expired = await api.Get(ReadPath, jwt, Token))
            Assert.Equal(new("1", null, true), await Read(expired));
        using (var cleared = await Send(api, jwt, null, "\"1\""))
            Assert.Equal(new("2", null, true), await Read(cleared));
        var actor = new JwtSecurityTokenHandler().ReadJwtToken(jwt).Claims.Single(c => c.Type == "oid").Value;
        await using var audit = database.DataSource.CreateCommand("""
            SELECT action,summary::text FROM audit_log
            WHERE actor=$1 AND target_kind='SystemNotice' AND target_id='global' ORDER BY id
            """);
        audit.Parameters.AddWithValue(actor);
        await using var reader = await audit.ExecuteReaderAsync(Token);
        Assert.True(await reader.ReadAsync(Token));
        Assert.Equal("system_notice.publish", reader.GetString(0));
        using (var summary = JsonDocument.Parse(reader.GetString(1)))
        {
            Assert.Equal(JsonValueKind.Null, summary.RootElement.GetProperty("before").ValueKind);
            Assert.Equal(notice, summary.RootElement.GetProperty("after").Deserialize(SystemNoticeJson.Default.SystemNotice));
        }
        Assert.True(await reader.ReadAsync(Token));
        Assert.Equal("system_notice.clear", reader.GetString(0));
        Assert.False(await reader.ReadAsync(Token));
    }

    [Theory]
    [InlineData(null, 401, 401)]
    [InlineData("", 200, 403)]
    [InlineData("Viewer", 200, 403)]
    [InlineData("Operator", 200, 403)]
    [InlineData("Registrar", 200, 403)]
    [InlineData("Agent", 200, 403)]
    [InlineData("Admin", 200, 200)]
    public async Task Authenticated_reads_but_only_global_Admin_can_publish_or_clear(string? role, int readStatus, int writeStatus)
    {
        await Reset();
        await using var api = await MetricsApiServer.Start(database.DataSource, Token);
        var jwt = role is null ? null : api.Jwt(role: role);
        using var read = await api.Get(ReadPath, jwt, Token);
        Assert.Equal(readStatus, (int)read.StatusCode);
        if (readStatus == 200) Assert.Equal(role == "Admin", (await Read(read)).CanManage);
        using var put = await Send(api, jwt, Notice(), "\"0\"");
        Assert.Equal(writeStatus, (int)put.StatusCode);
        using var clear = await Send(api, jwt, null, role == "Admin" ? "\"1\"" : "\"0\"");
        Assert.Equal(writeStatus, (int)clear.StatusCode);
    }

    [Fact]
    public async Task Concurrent_edits_and_stale_clears_cannot_overwrite_another_Admin()
    {
        await Reset();
        await using var api = await MetricsApiServer.Start(database.DataSource, Token);
        var jwt = api.Jwt(role: "Admin");
        var writes = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Send(api, jwt, Notice($"Edit {i}"), "\"0\"")));
        try
        {
            Assert.Single(writes, r => r.StatusCode == HttpStatusCode.OK);
            Assert.Equal(7, writes.Count(r => r.StatusCode == HttpStatusCode.PreconditionFailed));
            using var clear = await Send(api, jwt, null, "\"0\"");
            Assert.Equal(HttpStatusCode.PreconditionFailed, clear.StatusCode);
            using var current = await api.Get(ReadPath, jwt, Token);
            Assert.NotNull((await Read(current)).Notice);
            Assert.Equal(1L, await AuditCount());
        }
        finally { foreach (var response in writes) response.Dispose(); }
    }

    [Theory]
    [InlineData(null, 428)]
    [InlineData("*", 400)]
    [InlineData("W/\"0\"", 400)]
    [InlineData("\"00\"", 400)]
    [InlineData("\"0\", \"1\"", 400)]
    [InlineData("\"9223372036854775808\"", 400)]
    public async Task Missing_or_invalid_preconditions_are_explicit(string? etag, int status)
    {
        await using var api = await MetricsApiServer.Start(database.DataSource, Token);
        using var response = await Send(api, api.Jwt(role: "Admin"), Notice(), etag);
        Assert.Equal(status, (int)response.StatusCode);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("""{"message":" ","severity":"Warning","expiresAt":null}""")]
    [InlineData("""{"message":"test","severity":"Success","expiresAt":null}""")]
    [InlineData("""{"message":"test","severity":"Info","expiresAt":"2000-01-01T00:00:00Z"}""")]
    [InlineData("""{"message":"test","severity":"Info","expiresAt":null,"unknown":1}""")]
    [InlineData("""{"message":"test","message":"other","severity":"Info","expiresAt":null}""")]
    public async Task Invalid_JSON_does_not_mutate_or_audit(string json)
    {
        await Reset();
        await using var api = await MetricsApiServer.Start(database.DataSource, Token);
        using var response = await Raw(api, api.Jwt(role: "Admin"), json);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0L, await AuditCount());
    }

    [Fact]
    public async Task Invalid_identity_oversize_content_type_query_and_delete_body_fail_explicitly()
    {
        await Reset();
        await using var api = await MetricsApiServer.Start(database.DataSource, Token);
        using (var invalidActor = await Send(api, api.Jwt(role: "Admin", actor: Guid.Empty.ToString()), Notice(), "\"0\""))
            Assert.Equal(HttpStatusCode.Forbidden, invalidActor.StatusCode);
        var jwt = api.Jwt(role: "Admin");
        using (var huge = await Raw(api, jwt, new string(' ', 16385)))
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, huge.StatusCode);
        using (var content = await Raw(api, jwt, "{}", "text/plain"))
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, content.StatusCode);
        using (var query = await api.Get(ReadPath + "?extra=1", jwt, Token))
            Assert.Equal(HttpStatusCode.BadRequest, query.StatusCode);
        using (var body = await Raw(api, jwt, "{}", method: HttpMethod.Delete))
            Assert.Equal(HttpStatusCode.BadRequest, body.StatusCode);
        using (var chunked = await Raw(api, jwt, new string(' ', 16385), chunked: true))
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, chunked.StatusCode);
        Assert.Equal(0L, await AuditCount());
    }

    [Fact]
    public async Task Audit_failure_rolls_back_notice_and_revision_and_missing_singleton_is_not_all_clear()
    {
        await Reset();
        await using var api = await MetricsApiServer.Start(database.DataSource, Token);
        var jwt = api.Jwt(role: "Admin");
        var name = "reject_notice_" + Guid.NewGuid().ToString("N");
        await Execute($"""
            CREATE FUNCTION {name}() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'injected notice audit failure'; END $$;
            CREATE TRIGGER {name} BEFORE INSERT ON audit_log FOR EACH ROW
                WHEN (NEW.target_kind='SystemNotice') EXECUTE FUNCTION {name}();
            """);
        try
        {
            using var failed = await Send(api, jwt, Notice(), "\"0\"");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
            Assert.DoesNotContain("injected", await failed.Content.ReadAsStringAsync(Token));
            using var current = await api.Get(ReadPath, jwt, Token);
            Assert.Equal(new("0", null, true), await Read(current));
        }
        finally { await Execute($"DROP TRIGGER {name} ON audit_log; DROP FUNCTION {name}();"); }
        await Execute("DELETE FROM system_notice");
        try
        {
            using var failed = await api.Get(ReadPath, jwt, Token);
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        }
        finally { await Execute("INSERT INTO system_notice(singleton) VALUES(true)"); }
    }

    private async Task<SystemNoticeResponse> Read(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(Token), SystemNoticeJson.Default.SystemNoticeResponse)!;
    }
    private Task<HttpResponseMessage> Send(MetricsApiServer api, string? jwt, SystemNotice? notice, string? etag) =>
        Raw(api, jwt, notice is null ? null : JsonSerializer.Serialize(notice, SystemNoticeJson.Default.SystemNotice),
            method: notice is null ? HttpMethod.Delete : HttpMethod.Put, etag: etag);
    private async Task<HttpResponseMessage> Raw(MetricsApiServer api, string? jwt, string? json, string type = "application/json",
        HttpMethod? method = null, string? etag = "\"0\"", bool chunked = false)
    {
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        using var request = new HttpRequestMessage(method ?? HttpMethod.Put, api.Api + WritePath);
        if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, type);
        if (jwt is not null) request.Headers.Authorization = new("Bearer", jwt);
        if (etag is not null) request.Headers.TryAddWithoutValidation("If-Match", etag);
        if (chunked) request.Headers.TransferEncodingChunked = true;
        return await client.SendAsync(request, Token);
    }
    private async Task Execute(string sql)
    {
        await using var command = database.DataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(Token);
    }
    private Task Reset() => Execute("UPDATE system_notice SET revision=0,message=NULL,severity=NULL,expires_at=NULL; DELETE FROM audit_log WHERE target_kind='SystemNotice'");
    private async Task<long> AuditCount()
    {
        await using var query = database.DataSource.CreateCommand("SELECT count(*) FROM audit_log WHERE target_kind='SystemNotice'");
        return (long)(await query.ExecuteScalarAsync(Token))!;
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
