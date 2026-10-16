using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Microsoft.IdentityModel.Validators;
using Npgsql;
using Wf.Contracts.Registry;
using Wf.ControlPlane.Api;
using Wf.ControlPlane.Auth;
using Wf.ControlPlane.Data;
using Wf.ControlPlane.Live;
using Wf.ControlPlane.Registry;
using Wf.Integration.Tests.Agents;

namespace Wf.Integration.Tests.Auth;

public sealed partial class AuthorizationMatrixTests(AuthorizationDatabase database) : IClassFixture<AuthorizationDatabase>
{
    public static IEnumerable<object[]> Roles =>
        from held in Enum.GetValues<RegistryRole>()
        from required in Enum.GetValues<RegistryRole>()
        from teamScope in new[] { false, true }
        select new object[] { held, required, teamScope };

    [Theory]
    [MemberData(nameof(Roles))]
    public async Task Http_matrix_checks_every_role_against_stored_team_type_run_and_series(
        RegistryRole held, RegistryRole required, bool teamScope)
    {
        var group = Guid.NewGuid();
        var seed = await Seed(held, group, teamScope);
        await using var server = await Server.Start(database.DataSource);
        var jwt = server.Token(group: group);
        var expected = held == RegistryRole.Admin || held == required || required == RegistryRole.Viewer;
        foreach (var (kind, id) in seed.InScope)
        {
            var permitted = expected && (kind != "team" || teamScope);
            using var response = await server.Get(kind, required, id, jwt);
            await Check(response, permitted);
        }
        foreach (var (kind, id) in seed.OtherTeam)
        {
            using var response = await server.Get(kind, required, id, jwt);
            await Check(response, false);
        }
        using var sibling = await server.Get("type", required, seed.Team + "/sibling", jwt);
        await Check(sibling, expected && teamScope);
    }

    [Theory]
    [InlineData(RegistryRole.Viewer, false)]
    [InlineData(RegistryRole.Operator, false)]
    [InlineData(RegistryRole.Author, false)]
    [InlineData(RegistryRole.Admin, false)]
    [InlineData(RegistryRole.Viewer, true)]
    [InlineData(RegistryRole.Operator, true)]
    [InlineData(RegistryRole.Author, true)]
    [InlineData(RegistryRole.Admin, true)]
    public async Task Caller_permissions_match_stored_roles_and_deny_other_scopes(RegistryRole held, bool teamScope)
    {
        var group = Guid.NewGuid();
        var seed = await Seed(held, group, teamScope);
        await using var server = await Server.Start(database.DataSource);
        var jwt = server.Token(group: group);
        foreach (var (kind, id) in seed.InScope)
        {
            using var response = await server.GetPath("/api/v1/permissions/" + kind + "?id=" + Uri.EscapeDataString(id), jwt);
            if (kind == "team" && !teamScope)
            {
                await Check(response, false);
                continue;
            }
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl!.NoStore);
            var permissions = await response.Content.ReadFromJsonAsync<CallerPermissions>(TestContext.Current.CancellationToken);
            Assert.Equal(new(kind, id, true, held is RegistryRole.Author or RegistryRole.Admin,
                held is RegistryRole.Operator or RegistryRole.Admin, held == RegistryRole.Admin), permissions);
        }
        foreach (var (kind, id) in seed.OtherTeam)
        {
            using var denied = await server.GetPath("/api/v1/permissions/" + kind + "?id=" + Uri.EscapeDataString(id), jwt);
            await Check(denied, false);
            Assert.True(denied.Headers.CacheControl!.NoStore);
        }
        using var sibling = await server.GetPath("/api/v1/permissions/type?id=" + Uri.EscapeDataString(seed.Team + "/sibling"), jwt);
        Assert.Equal(teamScope ? HttpStatusCode.OK : HttpStatusCode.Forbidden, sibling.StatusCode);
    }

    [Fact]
    public async Task Caller_permissions_recheck_revoked_bindings_without_token_renewal_and_fail_closed_on_invalid_resources()
    {
        var group = Guid.NewGuid();
        var seed = await Seed(RegistryRole.Operator, group, false);
        await using var server = await Server.Start(database.DataSource);
        var jwt = server.Token(group: group);
        var path = "/api/v1/permissions/type?id=" + Uri.EscapeDataString(seed.Type);
        using (var before = await server.GetPath(path, jwt)) Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        await Save(new(seed.Team, [new(seed.Team, "release", "description", new([]))]));
        using (var revoked = await server.GetPath(path, jwt)) await Check(revoked, false);
        foreach (var resource in new[] { "run?id=invalid", "series?id=" + Guid.NewGuid(), "type?id=absent/type" })
        {
            using var missing = await server.GetPath("/api/v1/permissions/" + resource, server.Token(role: "Admin"));
            await Check(missing, false);
        }
        foreach (var resource in new[] { "Type?id=team/type", "type?id=", "type?id=%0A", "type?id=" + new string('a', 1025) })
        {
            using var invalid = await server.GetPath("/api/v1/permissions/" + resource, jwt);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        using (var anonymous = await server.GetPath(path, null)) Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var globalAdmin = await server.GetPath(path, server.Token(identity: "app", scope: null, role: "Admin"));
        Assert.Equal(new("type", seed.Type, true, true, true, true),
            await globalAdmin.Content.ReadFromJsonAsync<CallerPermissions>(TestContext.Current.CancellationToken));
    }

    [Fact]
    [Trait("Category", "FrontendBrowser")]
    public async Task Caller_permissions_support_actual_browsers_with_signed_jwt_and_no_api_mocks()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("WF_PERMISSION_BROWSER") == "true",
            "Set WF_PERMISSION_BROWSER=true and WF_FRONTEND_ROOT after installing frontend dependencies and Playwright browsers.");
        var frontend = Environment.GetEnvironmentVariable("WF_FRONTEND_ROOT");
        Assert.True(frontend is not null && File.Exists(Path.Combine(frontend, "scripts", "permission-browser.ts")),
            "WF_FRONTEND_ROOT must identify the repository frontend directory.");
        var group = Guid.NewGuid();
        var viewer = await Seed(RegistryRole.Viewer, group, false);
        var @operator = await Seed(RegistryRole.Operator, group, false);
        await using var server = await Server.Start(database.DataSource);
        var start = new ProcessStartInfo("bun")
        {
            WorkingDirectory = frontend, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(Path.Combine("scripts", "permission-browser.ts"));
        start.Environment["WF_PERMISSION_API"] = server.Api;
        start.Environment["WF_PERMISSION_TOKEN"] = server.Token(group: group);
        start.Environment["WF_PERMISSION_VIEWER_WORKFLOW"] = viewer.Type + "/workflow";
        start.Environment["WF_PERMISSION_OPERATOR_WORKFLOW"] = @operator.Type + "/workflow";
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(90));
            await process.WaitForExitAsync(deadline.Token);
            Assert.True(process.ExitCode == 0, $"Permission browsers failed.\n{await stdout}\n{await stderr}");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(RegistryRole.Viewer, false)]
    [InlineData(RegistryRole.Operator, false)]
    [InlineData(RegistryRole.Author, false)]
    [InlineData(RegistryRole.Admin, false)]
    [InlineData(RegistryRole.Viewer, true)]
    [InlineData(RegistryRole.Operator, true)]
    [InlineData(RegistryRole.Author, true)]
    [InlineData(RegistryRole.Admin, true)]
    public async Task Real_signalr_subscribe_applies_same_scope_and_never_joins_denied_group(RegistryRole held, bool teamScope)
    {
        var group = Guid.NewGuid();
        var seed = await Seed(held, group, teamScope);
        await using var server = await Server.Start(database.DataSource);
        await using var live = await server.Connect(server.Token(group: group));
        foreach (var (kind, id) in seed.InScope)
        {
            var response = await live.Subscribe(kind, id);
            Assert.Equal(kind == "team" && !teamScope, response.ValueKind != JsonValueKind.Null);
            if (kind == "team" && !teamScope) Assert.Equal(403, response.GetProperty("status").GetInt32());
        }
        foreach (var (kind, id) in seed.OtherTeam)
            Assert.Equal(403, (await live.Subscribe(kind, id)).GetProperty("status").GetInt32());
        Assert.Equal(400, (await live.Subscribe("logs:unknown", seed.Run.ToString())).GetProperty("status").GetInt32());
        Assert.Equal(403, (await live.Subscribe("machines", "*")).GetProperty("status").GetInt32());

        var hub = server.App.Services.GetRequiredService<IHubContext<LiveHub>>();
        foreach (var (kind, id) in seed.OtherTeam)
            await hub.Clients.Group(LiveHub.Group(kind, id)).SendAsync("delta", "forbidden", TestContext.Current.CancellationToken);
        await hub.Clients.Group(LiveHub.Group("run", seed.Run.ToString())).SendAsync("delta", "allowed", TestContext.Current.CancellationToken);
        var received = await live.Read();
        Assert.Equal("delta", received.GetProperty("target").GetString());
        Assert.Equal("allowed", received.GetProperty("arguments")[0].GetString());
    }

    [Fact]
    public async Task Registration_revocation_is_visible_on_next_http_request_and_signalr_subscribe_without_reconnect()
    {
        var group = Guid.NewGuid();
        var seed = await Seed(RegistryRole.Viewer, group, true);
        await using var server = await Server.Start(database.DataSource);
        var jwt = server.Token(group: group);
        await using var live = await server.Connect(jwt);
        Assert.Equal(JsonValueKind.Null, (await live.Subscribe("type", seed.Type)).ValueKind);
        using (var before = await server.Get("type", RegistryRole.Viewer, seed.Type, jwt)) await Check(before, true);
        await Save(new(seed.Team, [new(seed.Team, "release", "description", new([]))]));
        using var after = await server.Get("type", RegistryRole.Viewer, seed.Type, jwt);
        await Check(after, false);
        Assert.Equal(403, (await live.Subscribe("type", seed.Type)).GetProperty("status").GetInt32());
        await server.App.Services.GetRequiredService<IHubContext<LiveHub>>().Clients.Group(LiveHub.Group("type", seed.Type))
            .SendAsync("delta", "revoked", TestContext.Current.CancellationToken);
        Assert.Equal(403, (await live.Subscribe("machines", "*")).GetProperty("status").GetInt32());
    }

    [Theory]
    [InlineData("app", null, "Agent")]
    [InlineData("app", null, "Registrar")]
    [InlineData("app", null, "")]
    [InlineData("app", null, "Viewer")]
    [InlineData("app", "api.read", "Agent")]
    [InlineData(null, null, "")]
    [InlineData(null, null, "Viewer")]
    [InlineData("user", "api.read", "Agent")]
    [InlineData("user", "api.read", "Registrar")]
    public async Task Workload_and_ambiguous_tokens_cannot_inherit_user_or_group_grants(string? identity, string? scope, string role)
    {
        var group = Guid.NewGuid();
        var user = Guid.NewGuid();
        var seed = await Seed(RegistryRole.Admin, group, true);
        await Bind(seed.Team, RegistryRole.Admin, user, RolePrincipalKind.User);
        await using var server = await Server.Start(database.DataSource);
        var jwt = server.Token(group: group, user: user, identity: identity, scope: scope, role: role);
        using var response = await server.Get("type", RegistryRole.Viewer, seed.Type, jwt);
        if (scope is null && role.Length == 0)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            await Assert.ThrowsAsync<WebSocketException>(() => server.Connect(jwt));
            return;
        }
        await Check(response, false);
        await using var live = await server.Connect(jwt);
        Assert.Equal(403, (await live.Subscribe("type", seed.Type)).GetProperty("status").GetInt32());
    }

    [Theory]
    [InlineData("hasgroups", "true")]
    [InlineData("_claim_names", "{\"groups\":\"src1\"}")]
    [InlineData("groups", "malformed")]
    [InlineData("groups", "00000000-0000-0000-0000-000000000000")]
    public async Task Group_overage_and_malformed_claims_fail_closed(string claim, string value)
    {
        var group = Guid.NewGuid();
        var seed = await Seed(RegistryRole.Admin, group, true);
        await using var server = await Server.Start(database.DataSource);
        using var response = await server.Get("type", RegistryRole.Viewer, seed.Type,
            server.Token(group: group, extra: [new(claim, value)]));
        await Check(response, false);
    }

    [Fact]
    public async Task Explicit_oid_grant_is_distinct_from_group_claim_and_global_database_grants()
    {
        var id = Guid.NewGuid();
        var seed = await Seed(RegistryRole.Viewer, Guid.NewGuid(), true);
        await Bind(seed.Team, RegistryRole.Author, id, RolePrincipalKind.User);
        await Bind("*", RegistryRole.Admin, id, RolePrincipalKind.Group, RoleScopeKind.Global);
        await using var server = await Server.Start(database.DataSource);
        using (var user = await server.Get("type", RegistryRole.Author, seed.Type, server.Token(user: id)))
            await Check(user, true);
        using (var group = await server.Get("type", RegistryRole.Author, seed.Type, server.Token(group: id)))
            await Check(group, false);
        using var other = await server.Get("type", RegistryRole.Viewer, seed.OtherType, server.Token(user: id, group: id));
        await Check(other, false);
    }

    [Fact]
    public async Task Global_admin_bypasses_scope_roles_but_not_missing_resources_or_lowercase_role()
    {
        var seed = await Seed(RegistryRole.Viewer, Guid.NewGuid(), false);
        await using var server = await Server.Start(database.DataSource);
        var jwt = server.Token(identity: "app", scope: null, role: "Admin");
        foreach (var (kind, id) in seed.InScope.Concat(seed.OtherTeam).Append(("machines", "*")))
        {
            using var response = await server.Get(kind, RegistryRole.Admin, id, jwt);
            await Check(response, true);
        }
        using (var missing = await server.Get("type", RegistryRole.Viewer, seed.Team + "/absent", jwt))
            await Check(missing, false);
        using (var wrongCase = await server.Get("type", RegistryRole.Viewer, seed.Type, server.Token(role: "admin")))
            await Check(wrongCase, false);
        await using var live = await server.Connect(jwt);
        Assert.Equal(JsonValueKind.Null, (await live.Subscribe("machines", "*")).ValueKind);
        Assert.Equal(JsonValueKind.Null, (await live.Subscribe("run", seed.Run.ToString("D").ToUpperInvariant())).ValueKind);
        await server.App.Services.GetRequiredService<IHubContext<LiveHub>>().Clients
            .Group(LiveHub.Group("run", seed.Run.ToString("D"))).SendAsync("delta", "canonical", TestContext.Current.CancellationToken);
        Assert.Equal("canonical", (await live.Read()).GetProperty("arguments")[0].GetString());
    }

    [Fact]
    public async Task Http_metadata_is_required_and_caller_cannot_forge_scope_via_query_or_case()
    {
        var group = Guid.NewGuid();
        var seed = await Seed(RegistryRole.Admin, group, true);
        await using var server = await Server.Start(database.DataSource);
        var jwt = server.Token(group: group);
        using (var forged = await server.Get("type", RegistryRole.Viewer, seed.OtherType + "?team=" + seed.Team, jwt))
            await Check(forged, false);
        using (var caseMismatch = await server.Get("type", RegistryRole.Viewer, seed.Type.ToUpperInvariant(), jwt))
            await Check(caseMismatch, false);
        using var missingMetadata = await server.GetPath("/missing-metadata", jwt);
        await Check(missingMetadata, false);
    }

    [Fact]
    public async Task Unknown_runs_cross_scope_series_and_unresolved_series_rows_fail_closed()
    {
        var group = Guid.NewGuid();
        var seed = await Seed(RegistryRole.Viewer, group, true);
        await using var server = await Server.Start(database.DataSource);
        var jwt = server.Token(group: group);
        using (var malformed = await server.Get("run", RegistryRole.Viewer, "not-a-uuid", jwt)) await Check(malformed, false);
        using (var missing = await server.Get("run", RegistryRole.Viewer, Guid.CreateVersion7().ToString(), jwt))
            await Check(missing, false);
        await Run(seed.OtherType, seed.Series);
        using (var mixed = await server.Get("series", RegistryRole.Viewer, seed.Series.ToString(), jwt)) await Check(mixed, false);
        var unresolvedSeries = Guid.CreateVersion7();
        await Run(seed.Type, unresolvedSeries);
        await Run("unregistered/missing", unresolvedSeries);
        using var unresolved = await server.Get("series", RegistryRole.Viewer, unresolvedSeries.ToString(), jwt);
        await Check(unresolved, false);
    }

    [Fact]
    public async Task ScheduleFileApplier_series_authorization_uses_permanent_owner_before_first_run_and_after_removal()
    {
        var group = Guid.NewGuid();
        var seed = await Seed(RegistryRole.Viewer, group, true);
        var schedule = Guid.CreateVersion7();
        var series = Guid.CreateVersion7();
        await using (var insert = database.DataSource.CreateCommand("""
            INSERT INTO schedules(id,workflow_id,origin,code_key,name,timezone,interval_s,created_by,updated_by)
            VALUES($1,$2,0,'stable','schedule','Etc/UTC',60,'test','test')
            """))
        {
            insert.Parameters.AddWithValue(schedule);
            insert.Parameters.AddWithValue(seed.Type + "/workflow");
            await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        await using (var insert = database.DataSource.CreateCommand(
            "INSERT INTO run_series(id,workflow_id,schedule_id) VALUES($1,$2,$3)"))
        {
            insert.Parameters.AddWithValue(series);
            insert.Parameters.AddWithValue(seed.Type + "/workflow");
            insert.Parameters.AddWithValue(schedule);
            await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        await using var server = await Server.Start(database.DataSource);
        var jwt = server.Token(group: group);
        using (var response = await server.Get("series", RegistryRole.Viewer, series.ToString(), jwt))
            await Check(response, true);
        using (var denied = await server.Get("series", RegistryRole.Viewer, series.ToString(), server.Token(group: Guid.NewGuid())))
            await Check(denied, false);
        await using var live = await server.Connect(jwt);
        Assert.Equal(JsonValueKind.Null, (await live.Subscribe("series", series.ToString())).ValueKind);
        await using (var remove = database.DataSource.CreateCommand("UPDATE schedules SET deleted_at=now(),paused=true WHERE id=$1"))
        {
            remove.Parameters.AddWithValue(schedule);
            await remove.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        using (var historical = await server.Get("series", RegistryRole.Viewer, series.ToString(), jwt))
            await Check(historical, true);
        await Run(seed.OtherType, series);
        using var ambiguous = await server.Get("series", RegistryRole.Viewer, series.ToString(), jwt);
        await Check(ambiguous, false);
    }

    [Fact]
    public async Task Missing_authentication_is_401_and_query_tokens_work_only_on_hub_segment_with_header_precedence()
    {
        var seed = await Seed(RegistryRole.Viewer, Guid.NewGuid(), false);
        await using var server = await Server.Start(database.DataSource);
        using (var anonymous = await server.GetPath("/matrix/type/Viewer/" + seed.Type, null))
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        var jwt = server.Token(role: "Admin");
        using (var query = await server.GetPath("/matrix/type/Viewer/" + seed.Type + "?access_token=" + jwt, null))
            Assert.Equal(HttpStatusCode.Unauthorized, query.StatusCode);
        using (var similarlyNamed = await server.GetPath("/hubs/live/v1-evil?access_token=" + jwt, null))
            Assert.Equal(HttpStatusCode.Unauthorized, similarlyNamed.StatusCode);
        using (var precedence = await server.GetPath(LiveHub.Path + "/negotiate?negotiateVersion=1&access_token=" + jwt,
            "invalid", HttpMethod.Post))
            Assert.Equal(HttpStatusCode.Unauthorized, precedence.StatusCode);
        using var unsigned = await server.Get("type", RegistryRole.Viewer, seed.Type, server.Token(role: "Admin", unsigned: true));
        Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);
    }

    [Fact]
    public async Task Typed_authorization_uses_same_handler_and_cancellation_is_not_silenced()
    {
        var group = Guid.NewGuid();
        var seed = await Seed(RegistryRole.Viewer, group, false);
        await using var server = await Server.Start(database.DataSource);
        using var scope = server.App.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();
        var caller = new ClaimsPrincipal(new ClaimsIdentity(
            [new("oid", Guid.NewGuid().ToString()), new("idtyp", "user"), new("groups", group.ToString())], "test"));
        Assert.True((await service.AuthorizeAsync(caller, new TeamResource(TeamResourceKind.Type, seed.Type),
            TeamAuthorizationExtensions.Policy(RegistryRole.Viewer))).Succeeded);
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.AuthorizeAsync(caller,
            new TeamResource(TeamResourceKind.Type, seed.Type, cancel.Token), TeamAuthorizationExtensions.Policy(RegistryRole.Viewer)));
    }

    [Fact]
    public async Task Maximum_unicode_type_id_and_delegated_user_without_idtyp_remain_authorized()
    {
        var team = string.Concat(Guid.NewGuid().ToByteArray().Select(value => char.ConvertFromUtf32(0x1F300 + value))) +
            string.Concat(Enumerable.Repeat("\U0001F680", 112));
        var type = "a" + string.Concat(Enumerable.Repeat("\U0001F680", 127));
        var group = Guid.NewGuid();
        await Save(new(team, [new(team, type, "description", new([]), [new(RegistryRole.Viewer, group)])]));
        await using var server = await Server.Start(database.DataSource);
        var jwt = server.Token(group: group, identity: null);
        using var response = await server.Get("type", RegistryRole.Viewer, team + "/" + type, jwt);
        await Check(response, true);
        await using var live = await server.Connect(jwt);
        Assert.Equal(JsonValueKind.Null, (await live.Subscribe("type", team + "/" + type)).ValueKind);
    }

    private static async Task Check(HttpResponseMessage response, bool allowed)
    {
        Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.Forbidden, response.StatusCode);
        if (allowed) return;
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(TestContext.Current.CancellationToken);
        Assert.Equal(403, problem!.Status);
        Assert.Equal(TeamRoleHandler.DenialReason, problem.Detail);
        Assert.Equal("insufficient_scope_role", Assert.IsType<JsonElement>(problem.Extensions["reason"]).GetString());
    }

    private async Task<Seeded> Seed(RegistryRole role, Guid group, bool teamScope)
    {
        var team = "auth-" + Guid.NewGuid().ToString("N");
        var other = team + "-other";
        var binding = new RegistryRoleBinding(role, group);
        await Save(new(team,
            [new(team, "release", "description", new([]), teamScope ? [] : [binding]),
             new(team, "sibling", "description", new([]))],
            RoleBindings: teamScope ? [binding] : []));
        await Save(new(other, [new(other, "release", "description", new([]))]));
        var series = Guid.CreateVersion7();
        var otherSeries = Guid.CreateVersion7();
        return new(team, other, await Run(team + "/release", series), await Run(other + "/release", otherSeries),
            series, otherSeries);
    }

    private async Task Save(TeamRegistry registry)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await new RegistryWriter().UpsertAsync(connection, transaction, registry, TestContext.Current.CancellationToken);
        await transaction.CommitAsync(TestContext.Current.CancellationToken);
    }

    private async Task<Guid> Run(string type, Guid series)
    {
        var run = Guid.CreateVersion7();
        await using var query = database.DataSource.CreateCommand("""
            INSERT INTO runs(run_day,id,workflow_id,version_id,series_id,trigger,triggered_by,variables,metadata,status,created_at)
            VALUES(uuid_v7_run_day($1),$1,$2,$3,$4,0,'matrix','{}','{}',0,now())
            """);
        query.Parameters.AddWithValue(run);
        query.Parameters.AddWithValue(type + "/workflow");
        query.Parameters.AddWithValue(Guid.CreateVersion7());
        query.Parameters.AddWithValue(series);
        await query.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return run;
    }

    private async Task Bind(string scope, RegistryRole role, Guid id, RolePrincipalKind principal,
        RoleScopeKind kind = RoleScopeKind.Team)
    {
        await using var query = database.DataSource.CreateCommand("""
            INSERT INTO role_bindings(id,scope_kind,scope_id,role,principal_id,principal_kind) VALUES($1,$2,$3,$4,$5,$6)
            """);
        query.Parameters.AddWithValue(Guid.CreateVersion7());
        query.Parameters.AddWithValue((short)kind);
        query.Parameters.AddWithValue(scope);
        query.Parameters.AddWithValue((short)role);
        query.Parameters.AddWithValue(id);
        query.Parameters.AddWithValue((short)principal);
        await query.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private sealed record Seeded(string Team, string Other, Guid Run, Guid OtherRun, Guid Series, Guid OtherSeries)
    {
        public string Type => Team + "/release";
        public string OtherType => Other + "/release";
        public (string, string)[] InScope => [("team", Team), ("type", Type), ("run", Run.ToString()), ("series", Series.ToString())];
        public (string, string)[] OtherTeam => [("team", Other), ("type", OtherType), ("run", OtherRun.ToString()), ("series", OtherSeries.ToString())];
    }

    private sealed class Server(WebApplication app, HttpClient client, RSA rsa, RsaSecurityKey key) : IAsyncDisposable
    {
        private const string Tenant = "ef8a05a0-451c-4479-9c8d-391b6bc7ee1a";
        private const string Audience = "562b0870-4522-43da-8587-41e9b788f309";
        private const string Issuer = $"https://login.microsoftonline.com/{Tenant}/v2.0";
        public WebApplication App => app;
        public string Api => client.BaseAddress!.AbsoluteUri;

        public static async Task<Server> Start(NpgsqlDataSource source)
        {
            var rsa = RSA.Create(2048);
            var key = new RsaSecurityKey(rsa) { KeyId = "matrix-test" };
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Configuration["AzureAd:TenantId"] = Tenant;
            builder.Configuration["AzureAd:ClientId"] = Audience;
            builder.Services.AddWorkflowAuthentication(builder.Configuration, builder.Environment);
            builder.Services.AddSingleton(source);
            builder.Services.AddTeamAuthorization();
            builder.Services.AddWorkflowOpenApi();
            builder.Services.AddSignalR();
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
            app.UseAuthentication();
            app.UseAuthorization();
            foreach (var role in Enum.GetValues<RegistryRole>())
            foreach (var kind in Enum.GetValues<TeamResourceKind>())
                app.MapGet($"/matrix/{kind.ToString().ToLowerInvariant()}/{role}/{{**id}}", () => Results.Ok())
                    .RequireTeamRole(role, kind, "id");
            app.MapGet("/missing-metadata", () => Results.Ok()).RequireAuthorization(TeamAuthorizationExtensions.Policy(RegistryRole.Viewer));
            app.MapPermissionsEndpoints();
            app.MapRegistryEndpoints();
            app.MapCatalogEndpoints();
            app.MapWorkflowOpenApi();
            app.MapGet("/hubs/live/v1-evil", () => Results.Ok());
            app.MapHub<LiveHub>(LiveHub.Path, options => options.CloseOnAuthenticationExpiration = true);
            await app.StartAsync(TestContext.Current.CancellationToken);
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return new(app, new HttpClient { BaseAddress = new(address), Timeout = TimeSpan.FromSeconds(10) }, rsa, key);
        }

        public string Token(Guid? group = null, Guid? user = null, string? identity = "user", string? scope = "api.read",
            string role = "", Claim[]? extra = null, bool unsigned = false)
        {
            var claims = new List<Claim>
            {
                new("tid", Tenant), new("ver", "2.0"), new("oid", (user ?? Guid.NewGuid()).ToString()),
                new("azp", "60d24b19-f65a-4ed4-96cc-9174bfcacabc")
            };
            if (group is not null) claims.Add(new("groups", group.ToString()!));
            if (identity is not null) claims.Add(new("idtyp", identity));
            if (scope is not null) claims.Add(new("scp", scope));
            if (role.Length != 0) claims.Add(new("roles", role));
            if (extra is not null) claims.AddRange(extra);
            return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(Issuer, Audience, claims,
                DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(10),
                unsigned ? null : new SigningCredentials(key, SecurityAlgorithms.RsaSha256)));
        }

        public Task<HttpResponseMessage> Get(string kind, RegistryRole role, string id, string jwt) =>
            GetPath($"/matrix/{kind}/{role}/{id}", jwt);

        public async Task<HttpResponseMessage> GetPath(string path, string? jwt, HttpMethod? method = null)
        {
            using var request = new HttpRequestMessage(method ?? HttpMethod.Get, path);
            if (jwt is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
            return await client.SendAsync(request, TestContext.Current.CancellationToken);
        }

        public async Task<LiveClient> Connect(string jwt)
        {
            var socket = new ClientWebSocket();
            await socket.ConnectAsync(new UriBuilder(new Uri(client.BaseAddress!, LiveHub.Path))
                { Scheme = "ws", Query = "access_token=" + jwt }.Uri, TestContext.Current.CancellationToken);
            var live = new LiveClient(socket);
            await live.Send(new { protocol = "json", version = 1 });
            Assert.Equal("{}", (await live.Read()).GetRawText());
            return live;
        }

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await app.DisposeAsync();
            rsa.Dispose();
        }
    }

    private sealed class LiveClient(ClientWebSocket socket) : IAsyncDisposable
    {
        private string pending = "";
        private int invocation;
        private readonly CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        public async Task Send<T>(T value)
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value) + "\u001e"),
                WebSocketMessageType.Text, true, timeout.Token);
        }

        public async Task<JsonElement> Subscribe(string scope, string id)
        {
            var current = (++invocation).ToString(System.Globalization.CultureInfo.InvariantCulture);
            await Send(new { type = 1, invocationId = current, target = "Subscribe", arguments = new[] { scope, id } });
            var reply = await Read();
            Assert.Equal(3, reply.GetProperty("type").GetInt32());
            Assert.Equal(current, reply.GetProperty("invocationId").GetString());
            Assert.False(reply.TryGetProperty("error", out _), reply.ToString());
            return reply.GetProperty("result").Clone();
        }

        public async Task<JsonElement> Read()
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            while (true)
            {
                var end = pending.IndexOf('\u001e');
                if (end >= 0)
                {
                    var json = pending[..end];
                    pending = pending[(end + 1)..];
                    using var document = JsonDocument.Parse(json);
                    if (document.RootElement.TryGetProperty("type", out var type) && type.GetInt32() == 6) continue;
                    return document.RootElement.Clone();
                }
                var buffer = new byte[8192];
                var received = await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token);
                Assert.NotEqual(WebSocketMessageType.Close, received.MessageType);
                pending += Encoding.UTF8.GetString(buffer, 0, received.Count);
                Assert.True(pending.Length <= 65536);
            }
        }

        public ValueTask DisposeAsync()
        {
            socket.Abort();
            socket.Dispose();
            timeout.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

public sealed class AuthorizationDatabase : AgentDatabase
{
    protected override IReadOnlyList<string> Migrations => Migrator.LoadScripts().Select(script => script.Name).ToArray();
}
