using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Wf.Contracts.Registry;
using Wf.ControlPlane.Api;
using Wf.ControlPlane.Api.Machines;

namespace Wf.Integration.Tests.Auth;

public sealed partial class AuthorizationMatrixTests
{
    [Fact]
    public async Task AuditApi_requires_current_global_Admin_and_rejects_Viewer_and_team_Admin()
    {
        await using var server = await AuditServer();
        foreach (var role in new[] { "", "Viewer", "Operator", "Author", "Agent", "Registrar", "admin" })
        {
            using var denied = await server.GetPath("/api/v1/audit?actor=private", server.Token(role: role));
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            Assert.True(denied.Headers.CacheControl!.NoStore);
            Assert.DoesNotContain("private", await denied.Content.ReadAsStringAsync(MachineToken));
        }
        var group = Guid.NewGuid();
        await Seed(RegistryRole.Admin, group, true);
        using (var teamAdmin = await server.GetPath("/api/v1/audit", server.Token(group: group)))
            Assert.Equal(HttpStatusCode.Forbidden, teamAdmin.StatusCode);
        using (var anonymous = await server.GetPath("/api/v1/audit", null))
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using (var unsigned = await server.GetPath("/api/v1/audit", server.Token(role: "Admin", unsigned: true)))
            Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);
        foreach (var identity in new[] { "user", "app" })
        {
            using var admin = await server.GetPath("/api/v1/audit?limit=1",
                server.Token(role: "Admin", identity: identity, scope: identity == "app" ? null : "api.read"));
            Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
            Assert.True(admin.Headers.CacheControl!.NoStore);
        }
    }

    [Fact]
    public async Task AuditApi_filters_exact_actor_target_and_half_open_time_and_pages_timestamp_ties_without_losing_int64()
    {
        await using var server = await AuditServer();
        var actor = "audit-" + Guid.NewGuid().ToString("N");
        var target = "target-" + Guid.NewGuid().ToString("N");
        var at = new DateTimeOffset(DateTimeOffset.UtcNow.Date, TimeSpan.Zero).AddHours(2).AddTicks(10);
        var older = await InsertAudit(actor, target, at.AddTicks(-10), """{"before":{"pool":"old"},"after":{"pool":"new"}}""");
        var first = await InsertAudit(actor, target, at);
        var second = await InsertAudit(actor, target, at);
        await InsertAudit(actor, target, at.AddTicks(10));
        await InsertAudit(actor + "-other", target, at);
        await InsertAudit(actor, target + "-other", at);
        var jwt = server.Token(role: "Admin");
        var query = "?actor=" + actor + "&targetKind=Machine&targetId=" + target +
            "&from=" + Uri.EscapeDataString(at.AddTicks(-10).ToString("O", CultureInfo.InvariantCulture)) +
            "&to=" + Uri.EscapeDataString(at.AddTicks(10).ToString("O", CultureInfo.InvariantCulture)) + "&limit=1";
        using var newest = await server.GetPath("/api/v1/audit" + query, jwt);
        var page = await ReadAudit(newest);
        Assert.Equal(second.ToString(CultureInfo.InvariantCulture), Assert.Single(page.Items).Id);
        Assert.Equal(at, page.Items[0].At);
        Assert.NotNull(page.NextCursor);
        // A new row ahead of the cursor cannot duplicate or displace the remaining older rows.
        await InsertAudit(actor, target, at);
        using var next = await server.GetPath("/api/v1/audit" + query + "&cursor=" + page.NextCursor, jwt);
        var middle = await ReadAudit(next);
        Assert.Equal(first.ToString(CultureInfo.InvariantCulture), Assert.Single(middle.Items).Id);
        using var last = await server.GetPath("/api/v1/audit" + query + "&cursor=" + middle.NextCursor, jwt);
        var tail = await ReadAudit(last);
        Assert.Equal(older.ToString(CultureInfo.InvariantCulture), Assert.Single(tail.Items).Id);
        Assert.Equal("old", tail.Items[0].Summary.GetProperty("before").GetProperty("pool").GetString());
        Assert.Equal("new", tail.Items[0].Summary.GetProperty("after").GetProperty("pool").GetString());
        Assert.Null(tail.NextCursor);
        foreach (var changed in new[] { query.Replace(actor, actor + "-other", StringComparison.Ordinal),
            query.Replace("limit=1", "limit=2", StringComparison.Ordinal), query.Replace("Machine", "Run", StringComparison.Ordinal) })
        {
            using var mismatch = await server.GetPath("/api/v1/audit" + changed + "&cursor=" + page.NextCursor, jwt);
            Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
        }
        foreach (var malformed in new[] { "?actor=a&actor=b", "?targetId=", "?unknown=value", "?limit=101", "?from=2026-10-17" })
        {
            using var invalid = await server.GetPath("/api/v1/audit" + malformed, jwt);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.True(invalid.Headers.CacheControl!.NoStore);
        }
        using var injection = await server.GetPath("/api/v1/audit?actor=" + Uri.EscapeDataString("' OR true --"), jwt);
        Assert.Empty((await ReadAudit(injection)).Items);
        var serialized = await last.Content.ReadAsStringAsync(MachineToken);
        foreach (var forbidden in new[] { "sessionId", "variables", "uploadUri", "storageKey" }) Assert.DoesNotContain(forbidden, serialized);
        using var document = await server.GetPath("/openapi/v1.json", jwt);
        using var json = JsonDocument.Parse(await document.Content.ReadAsStringAsync(MachineToken));
        var operation = json.RootElement.GetProperty("paths").GetProperty("/api/v1/audit").GetProperty("get");
        Assert.Equal(["actor", "cursor", "from", "limit", "targetId", "targetKind", "to"],
            operation.GetProperty("parameters").EnumerateArray().Select(value => value.GetProperty("name").GetString()).Order());
        Assert.True(operation.GetProperty("responses").TryGetProperty("403", out _));
    }

    [Fact]
    public async Task AuditApi_reads_actual_committed_machine_mutation_before_after_and_does_not_audit_reads()
    {
        await using var server = await Server.Start(database.DataSource,
            configure: app => { app.MapMachineEndpoints(); app.MapAuditEndpoints(); },
            configureServices: services => services.AddSingleton(TimeProvider.System));
        using var registry = new Wf.ControlPlane.Engine.Agents.AgentRegistry(database.DataSource, TimeProvider.System);
        var hello = MachineHello("audit-original", "linux");
        await registry.RegisterAsync(hello, MachineToken);
        var actor = Guid.NewGuid();
        var jwt = server.Token(role: "Admin", user: actor);
        using var mutation = await PatchMachine(server, hello.Id, jwt, """{"pool":"audit-updated"}""");
        Assert.Equal(HttpStatusCode.NoContent, mutation.StatusCode);
        var path = "/api/v1/audit?actor=" + actor.ToString("D") + "&targetKind=Machine&targetId=" + hello.Id;
        for (var index = 0; index < 2; index++)
        {
            using var response = await server.GetPath(path, jwt);
            var row = Assert.Single((await ReadAudit(response)).Items);
            Assert.Equal("machine.update", row.Action);
            Assert.Equal("User", row.ActorKind);
            Assert.Null(row.TeamId);
            Assert.Equal("audit-original", row.Summary.GetProperty("before").GetProperty("pool").GetString());
            Assert.Equal("audit-updated", row.Summary.GetProperty("after").GetProperty("pool").GetString());
        }
    }

    [Fact]
    public async Task AuditApi_preserves_actual_bigint_identity_and_sanitizes_unavailable_database_errors()
    {
        var actor = "bigint-" + Guid.NewGuid().ToString("N");
        await using (var insert = database.DataSource.CreateCommand("""
            INSERT INTO audit_log(id,actor,actor_kind,action,target_kind,target_id,summary) OVERRIDING SYSTEM VALUE
            VALUES(9223372036854775807,$1,'App','Register','Workflow','team/type/workflow','{}')
            """))
        {
            insert.Parameters.AddWithValue(actor);
            await insert.ExecuteNonQueryAsync(MachineToken);
        }
        await using (var server = await AuditServer())
        {
            using var response = await server.GetPath("/api/v1/audit?actor=" + actor, server.Token(role: "Admin"));
            var row = Assert.Single((await ReadAudit(response)).Items);
            Assert.Equal("9223372036854775807", row.Id);
            Assert.Equal("App", row.ActorKind);
        }
        await using var unavailable = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Database=audit-private-db;Username=audit-private-user;Password=audit-private-password;Timeout=1;Pooling=false");
        await using var failed = await Server.Start(unavailable, configure: app => app.MapAuditEndpoints());
        using var failure = await failed.GetPath("/api/v1/audit", failed.Token(role: "Admin"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failure.StatusCode);
        Assert.True(failure.Headers.CacheControl!.NoStore);
        var body = await failure.Content.ReadAsStringAsync(MachineToken);
        Assert.Contains("Audit trail unavailable", body);
        foreach (var secret in new[] { "audit-private", "127.0.0.1", "Npgsql", "Connection refused" })
            Assert.DoesNotContain(secret, body);
    }

    [Fact]
    [Trait("Category", "FrontendBrowser")]
    public async Task AuditApi_browser_filters_actual_Postgres_audit_and_denies_Viewer_with_real_jwt()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("WF_AUDIT_BROWSER") == "true",
            "Set WF_AUDIT_BROWSER=true and WF_FRONTEND_ROOT after installing dependencies and browsers.");
        var frontend = Environment.GetEnvironmentVariable("WF_FRONTEND_ROOT");
        Assert.True(frontend is not null && File.Exists(Path.Combine(frontend, "scripts", "audit-browser.ts")));
        await using var server = await AuditServer();
        var actor = "browser-audit-" + Guid.NewGuid().ToString("N");
        var target = "audit-target-" + Guid.NewGuid().ToString("N");
        var today = new DateTimeOffset(DateTimeOffset.UtcNow.Date, TimeSpan.Zero);
        for (var index = 0; index < 51; index++)
            await InsertAudit(actor, target, today.AddHours(2).AddTicks(index * 10),
                """{"before":{"pool":"old <script>"},"after":{"pool":"new & safe"}}""");
        await InsertAudit(actor, target, today.AddDays(-1).AddHours(2));
        await InsertAudit(actor + "-other", target, today.AddHours(2));
        var start = new ProcessStartInfo("bun")
        {
            WorkingDirectory = frontend, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(Path.Combine("scripts", "audit-browser.ts"));
        start.Environment["WF_AUDIT_API"] = server.Api;
        start.Environment["WF_AUDIT_FIXTURE"] = JsonSerializer.Serialize(new
        {
            actor, target, from = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            to = today.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            adminToken = server.Token(role: "Admin"), viewerToken = server.Token(role: "Viewer")
        });
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(MachineToken);
        var stderr = process.StandardError.ReadToEndAsync(MachineToken);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(MachineToken);
            deadline.CancelAfter(TimeSpan.FromMinutes(6));
            await process.WaitForExitAsync(deadline.Token);
            Assert.True(process.ExitCode == 0, $"Audit browsers failed.\n{await stdout}\n{await stderr}");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
        }
    }

    private Task<Server> AuditServer() => Server.Start(database.DataSource, configure: app => app.MapAuditEndpoints(),
        configureServices: services => services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, AuditJsonContext.Default)));

    private async Task<long> InsertAudit(string actor, string target, DateTimeOffset at, string summary = "{}")
    {
        await using var command = database.DataSource.CreateCommand("""
            INSERT INTO audit_log(ts,actor,actor_kind,action,target_kind,target_id,summary)
            VALUES($1,$2,'User','machine.update','Machine',$3,$4::jsonb) RETURNING id
            """);
        command.Parameters.AddWithValue(at);
        command.Parameters.AddWithValue(actor);
        command.Parameters.AddWithValue(target);
        command.Parameters.AddWithValue(summary);
        return (long)(await command.ExecuteScalarAsync(MachineToken))!;
    }

    private static async Task<AuditPage> ReadAudit(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        return (await response.Content.ReadFromJsonAsync(AuditJsonContext.Default.AuditPage, MachineToken))!;
    }
}
