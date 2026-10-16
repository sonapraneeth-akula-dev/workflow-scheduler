using System.Collections.Immutable;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Microsoft.IdentityModel.Validators;
using Npgsql;
using NpgsqlTypes;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Contracts.Registry;
using Wf.ControlPlane.Api;
using Wf.ControlPlane.Auth;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Registry;
using Wf.Engine.Core;

namespace Wf.Integration.Tests.Auth;

public sealed class RunQueryTests(AuthorizationDatabase database) : IClassFixture<AuthorizationDatabase>
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
    public async Task RunQuery_filters_every_page_by_fresh_team_or_exact_type_viewer_grants(RegistryRole role, bool teamScope)
    {
        var seed = await Seed(role, teamScope);
        var own = await Run(seed.Type + "/workflow");
        var sibling = await Run(seed.Team + "/sibling/workflow");
        await Run(seed.Other + "/release/workflow");
        await using var server = await Server.Start(database.DataSource);
        var jwt = server.Jwt(seed.Group);
        using var response = await server.Get("/api/v1/runs?team=" + seed.Team, jwt);
        var page = await Page(response);
        Assert.Equal(teamScope ? 2 : 1, page.Items.Count);
        Assert.Contains(page.Items, item => item.Id == own);
        Assert.Equal(teamScope, page.Items.Any(item => item.Id == sibling));
        Assert.All(page.Items, item => Assert.Equal(seed.Team, item.Team));
        using var foreign = await server.Get("/api/v1/runs?team=" + seed.Other, jwt);
        Assert.Empty((await Page(foreign)).Items);
        using var scoped = await server.Get("/api/v1/runs?team=" + seed.Team + "&type=sibling", jwt);
        Assert.Equal(teamScope ? 1 : 0, (await Page(scoped)).Items.Count);
        using var general = await server.Get("/api/v1/runs", jwt);
        Assert.Equal(teamScope ? 2 : 1, (await Page(general)).Items.Count);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        foreach (var forbidden in new[] { seed.Other, "variables", "values", "metadata", "manifest", "snapshot",
            "sourceUri", "packageUri", "payload", "outputs", "roleBindings", "secret-run-value", "blob://hidden" })
            Assert.DoesNotContain(forbidden, text);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task RunQuery_combines_metadata_status_type_series_workflow_and_inclusive_exclusive_created_time()
    {
        var seed = await Seed();
        var from = DateTimeOffset.UtcNow.AddMinutes(-10);
        from = new(from.Ticks / 10 * 10, TimeSpan.Zero);
        var to = from.AddMinutes(1);
        var series = Guid.CreateVersion7();
        var workflow = seed.Type + "/workflow";
        var match = await Run(workflow, RunStatus.Succeeded, from, series, """{"Type":"release","Values":{"env":"prod","region":"west"}}""");
        await Run(workflow, RunStatus.Succeeded, to, series, """{"Type":"release","Values":{"env":"prod","region":"west"}}""");
        await Run(workflow, RunStatus.Succeeded, from.AddTicks(-10), series, """{"Type":"release","Values":{"env":"prod","region":"west"}}""");
        await Run(workflow, RunStatus.Failed, from, series, """{"Type":"release","Values":{"env":"prod","region":"west"}}""");
        await Run(workflow, RunStatus.Succeeded, from, series, """{"Type":"release","Values":{"env":"dev","region":"west"}}""");
        await Run(workflow, RunStatus.Succeeded, from, series, """{"Type":"release","Values":{"env":"prod","region":"east"}}""");
        await Run(workflow, RunStatus.Succeeded, from, Guid.CreateVersion7(), """{"Type":"release","Values":{"env":"prod","region":"west"}}""");
        await Run(seed.Type + "/another", RunStatus.Succeeded, from, series, """{"Type":"release","Values":{"env":"prod","region":"west"}}""");
        await Run(seed.Team + "/sibling/workflow", RunStatus.Succeeded, from, series, """{"Type":"sibling","Values":{"env":"prod","region":"west"}}""");
        await Run(seed.Other + "/release/workflow", RunStatus.Succeeded, from, null, """{"Type":"release","Values":{"env":"prod","region":"west"}}""");
        await using var server = await Server.Start(database.DataSource);
        var path = "/api/v1/runs?team=" + seed.Team + "&type=release&workflowId=" + Uri.EscapeDataString(workflow) +
            "&seriesId=" + series + "&status=Succeeded&meta.env=prod&meta.region=west&from=" +
            Uri.EscapeDataString(from.ToString("O", System.Globalization.CultureInfo.InvariantCulture)) +
            "&to=" + Uri.EscapeDataString(to.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        // The series deliberately spans two same-team types: its exact authorization fails closed.
        using (var ambiguous = await server.Get(path, server.Jwt(seed.Group))) await Forbidden(ambiguous);
        await using (var removeSibling = database.DataSource.CreateCommand("UPDATE runs SET series_id=NULL WHERE series_id=$1 AND workflow_id=$2"))
        {
            removeSibling.Parameters.AddWithValue(series);
            removeSibling.Parameters.AddWithValue(seed.Team + "/sibling/workflow");
            await removeSibling.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        using var response = await server.Get(path, server.Jwt(seed.Group));
        var item = Assert.Single((await Page(response)).Items);
        Assert.Equal(match, item.Id);
        Assert.Equal("Succeeded", item.Status);
        Assert.Equal(from, item.CreatedAt);
        Assert.Equal(series, item.SeriesId);
        Assert.Equal("release", item.Type);
    }

    [Fact]
    public async Task RunQuery_keyset_uuid_order_is_deterministic_on_ties_and_stable_across_newer_inserts()
    {
        var seed = await Seed();
        var at = DateTimeOffset.UtcNow;
        var prefix = Guid.CreateVersion7().ToString("D")[..24];
        Guid Id(int value) => Guid.Parse(prefix + value.ToString("x12", System.Globalization.CultureInfo.InvariantCulture));
        foreach (var index in Enumerable.Range(1, 5))
            await Run(seed.Type + "/workflow", RunStatus.Running, at, id: Id(index));
        await using var server = await Server.Start(database.DataSource);
        var jwt = server.Jwt(seed.Group);
        var path = "/api/v1/runs?team=" + seed.Team + "&type=release&status=Running&limit=2";
        using var firstResponse = await server.Get(path, jwt);
        var first = await Page(firstResponse);
        Assert.Equal([Id(5), Id(4)], first.Items.Select(item => item.Id));
        Assert.All(first.Items, item => Assert.Equal(first.Items[0].CreatedAt, item.CreatedAt));
        Assert.NotNull(first.NextCursor);
        await Run(seed.Type + "/workflow", RunStatus.Running, at, id: Id(6));
        await Run(seed.Type + "/workflow", RunStatus.Running, at.AddHours(-1));
        using var secondResponse = await server.Get(path + "&cursor=" + first.NextCursor, jwt);
        var second = await Page(secondResponse);
        Assert.Equal([Id(3), Id(2)], second.Items.Select(item => item.Id));
        using var lastResponse = await server.Get(path + "&cursor=" + second.NextCursor, jwt);
        var last = await Page(lastResponse);
        Assert.Equal(Id(1), Assert.Single(last.Items).Id);
        Assert.Null(last.NextCursor);
        Assert.Equal(5, first.Items.Concat(second.Items).Concat(last.Items).Select(item => item.Id).Distinct().Count());
    }

    [Fact]
    public async Task RunQuery_explicit_resources_deny_foreign_unknown_and_ambiguous_scopes_even_for_global_admin()
    {
        var seed = await Seed();
        var series = Guid.CreateVersion7();
        var foreignSeries = Guid.CreateVersion7();
        await Run(seed.Type + "/workflow", series: series);
        await Run(seed.Other + "/release/workflow", series: foreignSeries);
        await using var server = await Server.Start(database.DataSource);
        var jwt = server.Jwt(seed.Group);
        foreach (var query in new[]
        {
            "?workflowId=" + seed.Other + "/release/workflow", "?workflowId=" + seed.Type + "/unknown",
            "?workflowId=unknown/release/workflow", "?seriesId=" + foreignSeries, "?seriesId=" + Guid.CreateVersion7()
        })
        {
            using var response = await server.Get("/api/v1/runs" + query, jwt);
            await Forbidden(response);
        }
        foreach (var query in new[] { "?workflowId=unknown/release/workflow", "?seriesId=" + Guid.CreateVersion7() })
        {
            using var response = await server.Get("/api/v1/runs" + query, server.Jwt(role: "Admin", identity: "app", scope: null));
            await Forbidden(response);
        }
        using (var known = await server.Get("/api/v1/runs?seriesId=" + series, jwt))
            Assert.Single((await Page(known)).Items);
        using (var admin = await server.Get("/api/v1/runs?team=" + seed.Other,
            server.Jwt(role: "Admin", identity: "app", scope: null)))
            Assert.Single((await Page(admin)).Items);
        using (var unknownType = await server.Get("/api/v1/runs?team=" + seed.Team + "&type=absent", server.Jwt(role: "Admin")))
            Assert.Empty((await Page(unknownType)).Items);
        await Run(seed.Other + "/release/ambiguous", series: series);
        using var ambiguous = await server.Get("/api/v1/runs?seriesId=" + series, server.Jwt(role: "Admin"));
        await Forbidden(ambiguous);
    }

    [Fact]
    public async Task RunQuery_rechecks_revocation_and_never_trusts_agent_registrar_or_group_overage_tokens()
    {
        var seed = await Seed();
        await Run(seed.Type + "/workflow");
        await Run(seed.Type + "/workflow");
        await using var server = await Server.Start(database.DataSource);
        var jwt = server.Jwt(seed.Group);
        using var firstResponse = await server.Get("/api/v1/runs?team=" + seed.Team + "&limit=1", jwt);
        var first = await Page(firstResponse);
        Assert.NotNull(first.NextCursor);
        foreach (var token in new[]
        {
            server.Jwt(seed.Group, identity: "app", role: "Agent"),
            server.Jwt(seed.Group, identity: "app", role: "Registrar"),
            server.Jwt(seed.Group, extra: [new("hasgroups", "true")]),
            server.Jwt(seed.Group, extra: [new("_claim_names", """{"groups":"src1"}""")]),
            server.Jwt(seed.Group, extra: [new("groups", "not-a-guid")])
        })
        {
            using var denied = await server.Get("/api/v1/runs?team=" + seed.Team, token);
            Assert.Empty((await Page(denied)).Items);
            using var explicitDenied = await server.Get("/api/v1/runs?workflowId=" + seed.Type + "/workflow", token);
            await Forbidden(explicitDenied);
        }
        using (var anonymous = await server.Get("/api/v1/runs", null)) Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using (var tampered = await server.Get("/api/v1/runs", jwt[..^3] + "xxx")) Assert.Equal(HttpStatusCode.Unauthorized, tampered.StatusCode);
        await Save(new(seed.Team, [new(seed.Team, "release", "description", new([])), new(seed.Team, "sibling", "description", new([]))]));
        using (var revoked = await server.Get("/api/v1/runs?team=" + seed.Team, jwt)) Assert.Empty((await Page(revoked)).Items);
        using (var scoped = await server.Get("/api/v1/runs?workflowId=" + seed.Type + "/workflow", jwt)) await Forbidden(scoped);
        using var stale = await server.Get("/api/v1/runs?team=" + seed.Team + "&limit=1&cursor=" + first.NextCursor, jwt);
        await Problem(stale, "invalid_run_cursor");
    }

    [Fact]
    public async Task RunQuery_uses_actual_persisted_run_metadata_and_keeps_deleted_workflow_history_without_values()
    {
        var seed = await Seed();
        var workflow = seed.Type + "/workflow";
        var id = Guid.CreateVersion7();
        var metadata = new WorkflowMetadata("release", ImmutableSortedDictionary<string, string>.Empty
            .Add("env", "prod").Add("query.key", "x' OR true --"));
        using var variable = JsonDocument.Parse("\"secret-run-value\"");
        var state = new RunState(id, [], metadata, _ => { },
            new Dictionary<string, JsonElement> { ["secret"] = variable.RootElement });
        await new RunStore(database.DataSource, NullLogger<RunStore>.Instance)
            .CreateAsync(state, workflow, Guid.CreateVersion7(), DateTimeOffset.UtcNow.AddHours(1), TestContext.Current.CancellationToken);
        await using (var command = database.DataSource.CreateCommand("INSERT INTO workflows(id,type_id,deleted_at) VALUES($1,$2,now())"))
        {
            command.Parameters.AddWithValue(workflow);
            command.Parameters.AddWithValue(seed.Type);
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        await Run(workflow, metadata: """{"Type":"release","Values":{"env":"dev"}}""");
        await using var server = await Server.Start(database.DataSource);
        var path = "/api/v1/runs?workflowId=" + workflow + "&status=Queued&meta.env=prod&meta.query.key=" + Uri.EscapeDataString("x' OR true --");
        using var response = await server.Get(path, server.Jwt(seed.Group));
        var row = Assert.Single((await Page(response)).Items);
        Assert.Equal(id, row.Id);
        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        foreach (var forbidden in new[] { "secret-run-value", "variables", "values", "metadata", "query.key", "snapshot", "payload", "blob" })
            Assert.DoesNotContain(forbidden, json);
        await using var hardDelete = database.DataSource.CreateCommand("DELETE FROM workflows WHERE id=$1");
        hardDelete.Parameters.AddWithValue(workflow);
        await hardDelete.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        using var history = await server.Get(path, server.Jwt(seed.Group));
        Assert.Equal(id, Assert.Single((await Page(history)).Items).Id);
    }

    [Fact]
    public async Task RunQuery_rejects_cursor_reuse_across_filters_limits_or_authorized_scopes()
    {
        var seed = await Seed();
        await Run(seed.Type + "/workflow", metadata: """{"Type":"release","Values":{"env":"prod"}}""");
        await Run(seed.Type + "/workflow", metadata: """{"Type":"release","Values":{"env":"prod"}}""");
        await using var server = await Server.Start(database.DataSource);
        var jwt = server.Jwt(seed.Group);
        var path = "/api/v1/runs?team=" + seed.Team + "&type=release&limit=1&meta.env=prod";
        using var response = await server.Get(path, jwt);
        var cursor = (await Page(response)).NextCursor;
        Assert.NotNull(cursor);
        foreach (var other in new[]
        {
            path.Replace("prod", "dev", StringComparison.Ordinal), path.Replace("limit=1", "limit=2", StringComparison.Ordinal),
            path + "&status=Queued", path + "&from=2020-01-01T00:00:00Z", "/api/v1/runs?limit=1",
            path.Replace(seed.Team, seed.Other, StringComparison.Ordinal)
        })
        {
            using var mismatch = await server.Get(other + "&cursor=" + cursor, jwt);
            await Problem(mismatch, "invalid_run_cursor");
        }
        using var noScope = await server.Get(path + "&cursor=" + cursor, server.Jwt());
        await Problem(noScope, "invalid_run_cursor");
        using var valid = await server.Get(path + "&cursor=" + cursor, jwt);
        Assert.Single((await Page(valid)).Items);
    }

    [Theory]
    [InlineData("?limit=101")]
    [InlineData("?limit=0")]
    [InlineData("?limit=01")]
    [InlineData("?limit=%2B1")]
    [InlineData("?limit=1&limit=1")]
    [InlineData("?status=Running&status=Failed")]
    [InlineData("?status=Running&Status=Running")]
    [InlineData("?status=running")]
    [InlineData("?status=1")]
    [InlineData("?Status=Running")]
    [InlineData("?team=x&team=x")]
    [InlineData("?type=release")]
    [InlineData("?team=..")]
    [InlineData("?workflowId=team/type")]
    [InlineData("?seriesId=00000000-0000-0000-0000-000000000000")]
    [InlineData("?from=2026-10-15")]
    [InlineData("?from=2026-10-15T00:00:00")]
    [InlineData("?from=2026-10-15T00:00:00.1234567Z")]
    [InlineData("?from=2026-10-15T00:00:00Z&to=2026-10-15T00:00:00Z")]
    [InlineData("?meta.key=a&meta.key=b")]
    [InlineData("?meta.=value")]
    [InlineData("?meta.key=bad%0Avalue")]
    [InlineData("?cursor=invalid")]
    [InlineData("?unsupported=ignored")]
    [InlineData("?abortedBy=principal")]
    public async Task RunQuery_http_invalid_filters_have_explicit_problem_details(string query)
    {
        await using var server = await Server.Start(database.DataSource);
        using var response = await server.Get("/api/v1/runs" + query, server.Jwt(role: "Admin"));
        await Problem(response, "invalid_run_query");
    }

    [Fact]
    public async Task RunQuery_actual_openapi_describes_safe_rows_named_statuses_timestamps_and_dynamic_metadata_filters()
    {
        await using var server = await Server.Start(database.DataSource);
        using var response = await server.Get("/openapi/v1.json", server.Jwt(role: "Admin"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var operation = document.RootElement.GetProperty("paths").GetProperty("/api/v1/runs").GetProperty("get");
        var parameters = operation.GetProperty("parameters").EnumerateArray()
            .ToDictionary(parameter => parameter.GetProperty("name").GetString()!, parameter => parameter);
        Assert.Equal(new[] { "cursor", "from", "limit", "meta.*", "seriesId", "status", "team", "to", "type", "workflowId" },
            parameters.Keys.Order(StringComparer.Ordinal));
        Assert.All(parameters.Values, parameter => Assert.Equal("query", parameter.GetProperty("in").GetString()));
        Assert.Equal("integer", parameters["limit"].GetProperty("schema").GetProperty("type").GetString());
        Assert.Equal(1, parameters["limit"].GetProperty("schema").GetProperty("minimum").GetInt32());
        Assert.Equal(100, parameters["limit"].GetProperty("schema").GetProperty("maximum").GetInt32());
        Assert.Equal(50, parameters["limit"].GetProperty("schema").GetProperty("default").GetInt32());
        Assert.Equal(Enum.GetNames<RunStatus>(), parameters["status"].GetProperty("schema").GetProperty("enum")
            .EnumerateArray().Select(value => value.GetString()));
        Assert.Equal("date-time", parameters["from"].GetProperty("schema").GetProperty("format").GetString());
        Assert.Equal("uuid", parameters["seriesId"].GetProperty("schema").GetProperty("format").GetString());
        Assert.Equal("string", parameters["meta.*"].GetProperty("schema").GetProperty("type").GetString());
        Assert.Contains("meta.environment=prod", parameters["meta.*"].GetProperty("description").GetString());
        Assert.Equal("#/components/schemas/RunQueryPage", operation.GetProperty("responses").GetProperty("200")
            .GetProperty("content").GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
        foreach (var code in new[] { "400", "401", "403", "500", "503" })
            Assert.True(operation.GetProperty("responses").TryGetProperty(code, out _));
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        var properties = schemas.GetProperty("RunQueryRow").GetProperty("properties");
        Assert.Equal(new[] { "createdAt", "endedAt", "id", "seriesId", "startedAt", "status", "team", "type", "versionId", "workflowId" },
            properties.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal("uuid", properties.GetProperty("id").GetProperty("format").GetString());
        Assert.Equal("date-time", properties.GetProperty("createdAt").GetProperty("format").GetString());
    }

    [Fact]
    public async Task RunQuery_migration_installs_partitioned_search_indexes_and_postgres_uses_type_status_and_metadata_indexes()
    {
        var seed = await Seed();
        await Run(seed.Type + "/workflow", RunStatus.Failed, metadata: """{"Type":"release","Values":{"env":"prod"}}""");
        await using (var query = database.DataSource.CreateCommand("""
            SELECT c.relname,c.relkind,count(i.inhrelid) FROM pg_class c LEFT JOIN pg_inherits i ON i.inhparent=c.oid
            WHERE c.relname LIKE 'runs_search_%' GROUP BY c.relname,c.relkind ORDER BY c.relname
            """))
        {
            await using var reader = await query.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            var names = new List<string>();
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                names.Add(reader.GetString(0));
                Assert.Equal('I', reader.GetChar(1));
                Assert.True(reader.GetInt64(2) >= 2);
            }
            Assert.Equal(new[] { "runs_search_created_id_desc_idx", "runs_search_id_desc_idx", "runs_search_status_id_desc_idx",
                "runs_search_type_id_desc_idx", "runs_search_type_status_id_desc_idx" }, names);
        }
        await using var connection = await database.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using (var setting = new NpgsqlCommand("SET LOCAL enable_seqscan=off", connection, transaction))
            await setting.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        foreach (var predicate in new[]
        {
            "split_part(workflow_id,'/',1)||'/'||split_part(workflow_id,'/',2)=$1 AND status=4",
            """metadata @> '{"Values":{"env":"prod"}}'::jsonb"""
        })
        {
            await using var query = new NpgsqlCommand("EXPLAIN (COSTS OFF) SELECT id FROM runs WHERE " + predicate +
                " ORDER BY id DESC LIMIT 51", connection, transaction);
            if (predicate.Contains("$1", StringComparison.Ordinal)) query.Parameters.AddWithValue(seed.Type);
            await using var reader = await query.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            var plan = new List<string>();
            while (await reader.ReadAsync(TestContext.Current.CancellationToken)) plan.Add(reader.GetString(0));
            Assert.Contains(plan, line => line.Contains("Index", StringComparison.Ordinal));
            Assert.DoesNotContain(plan, line => line.Contains("Seq Scan", StringComparison.Ordinal));
            if (predicate.StartsWith("metadata", StringComparison.Ordinal))
                Assert.Contains(plan, line => line.Contains("metadata", StringComparison.Ordinal) && line.Contains("idx", StringComparison.Ordinal));
        }
        await transaction.RollbackAsync(TestContext.Current.CancellationToken);
    }

    private async Task<Seeded> Seed(RegistryRole role = RegistryRole.Viewer, bool teamScope = false)
    {
        var team = "runquery-" + Guid.NewGuid().ToString("N");
        var group = Guid.NewGuid();
        var binding = new RegistryRoleBinding(role, group);
        await Save(new(team,
            [new(team, "release", "description", new([]), teamScope ? [] : [binding]),
             new(team, "sibling", "description", new([]))], RoleBindings: teamScope ? [binding] : []));
        await Save(new(team + "-other", [new(team + "-other", "release", "description", new([]))]));
        return new(team, team + "-other", group);
    }

    private async Task Save(TeamRegistry registry)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await new RegistryWriter().UpsertAsync(connection, transaction, registry, TestContext.Current.CancellationToken);
        await transaction.CommitAsync(TestContext.Current.CancellationToken);
    }

    private async Task<Guid> Run(string workflow, RunStatus status = RunStatus.Queued, DateTimeOffset? at = null,
        Guid? series = null, string metadata = """{"Type":"release","Values":{}}""", Guid? id = null)
    {
        var run = id ?? Guid.CreateVersion7();
        await using var query = database.DataSource.CreateCommand("""
            INSERT INTO runs(run_day,id,workflow_id,version_id,series_id,trigger,triggered_by,variables,metadata,status,
                created_at,started_at,ended_at)
            VALUES(uuid_v7_run_day($1),$1,$2,$3,$4,0,'run-query','{"secret":"secret-run-value","blob":"blob://hidden"}',$5,$6,$7,
                CASE WHEN $6::smallint>0 THEN $7 ELSE NULL END,
                CASE WHEN $6::smallint>=3 THEN $7 ELSE NULL END)
            """);
        query.Parameters.AddWithValue(run);
        query.Parameters.AddWithValue(workflow);
        query.Parameters.AddWithValue(Guid.CreateVersion7());
        query.Parameters.AddWithValue(NpgsqlDbType.Uuid, (object?)series ?? DBNull.Value);
        query.Parameters.AddWithValue(NpgsqlDbType.Jsonb, metadata);
        query.Parameters.AddWithValue((short)status);
        query.Parameters.AddWithValue(at ?? DateTimeOffset.UtcNow);
        await query.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return run;
    }

    private static async Task<RunQueryPage> Page(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        return (await response.Content.ReadFromJsonAsync(RunQueryJsonContext.Default.RunQueryPage,
            TestContext.Current.CancellationToken))!;
    }

    private static async Task Forbidden(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var problem = (await response.Content.ReadFromJsonAsync<ProblemDetails>(TestContext.Current.CancellationToken))!;
        Assert.Equal("insufficient_scope_role", Assert.IsType<JsonElement>(problem.Extensions["reason"]).GetString());
        Assert.Equal(TeamRoleHandler.DenialReason, problem.Detail);
    }

    private static async Task Problem(HttpResponseMessage response, string reason)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var problem = (await response.Content.ReadFromJsonAsync<ProblemDetails>(TestContext.Current.CancellationToken))!;
        Assert.Equal(400, problem.Status);
        Assert.Equal(reason, Assert.IsType<JsonElement>(problem.Extensions["reason"]).GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.Detail));
    }

    private sealed record Seeded(string Team, string Other, Guid Group)
    {
        public string Type => Team + "/release";
    }

    private sealed class Server(WebApplication app, HttpClient client, RSA rsa, RsaSecurityKey key) : IAsyncDisposable
    {
        private const string Tenant = "ef8a05a0-451c-4479-9c8d-391b6bc7ee1a";
        private const string Audience = "562b0870-4522-43da-8587-41e9b788f309";
        private const string Issuer = $"https://login.microsoftonline.com/{Tenant}/v2.0";

        public static async Task<Server> Start(NpgsqlDataSource source)
        {
            var rsa = RSA.Create(2048);
            var key = new RsaSecurityKey(rsa) { KeyId = "run-query-test" };
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Configuration["AzureAd:TenantId"] = Tenant;
            builder.Configuration["AzureAd:ClientId"] = Audience;
            builder.Services.AddWorkflowAuthentication(builder.Configuration, builder.Environment);
            builder.Services.AddSingleton(source);
            builder.Services.AddTeamAuthorization();
            builder.Services.AddWorkflowOpenApi();
            builder.Services.ConfigureHttpJsonOptions(options =>
                options.SerializerOptions.TypeInfoResolverChain.Insert(0, RunQueryJsonContext.Default));
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
            app.MapRunQueryEndpoints();
            app.MapWorkflowOpenApi();
            await app.StartAsync(TestContext.Current.CancellationToken);
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return new(app, new HttpClient { BaseAddress = new(address), Timeout = TimeSpan.FromSeconds(20) }, rsa, key);
        }

        public string Jwt(Guid? group = null, string? identity = "user", string? scope = "api.read",
            string role = "", Claim[]? extra = null)
        {
            var claims = new List<Claim> { new("tid", Tenant), new("ver", "2.0"), new("oid", Guid.NewGuid().ToString()),
                new("azp", "60d24b19-f65a-4ed4-96cc-9174bfcacabc") };
            if (group is not null) claims.Add(new("groups", group.ToString()!));
            if (identity is not null) claims.Add(new("idtyp", identity));
            if (scope is not null) claims.Add(new("scp", scope));
            if (role.Length > 0) claims.Add(new("roles", role));
            if (extra is not null) claims.AddRange(extra);
            return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(Issuer, Audience, claims,
                DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(10),
                new SigningCredentials(key, SecurityAlgorithms.RsaSha256)));
        }

        public async Task<HttpResponseMessage> Get(string path, string? jwt)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            if (jwt is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
            return await client.SendAsync(request, TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await app.DisposeAsync();
            rsa.Dispose();
        }
    }
}
