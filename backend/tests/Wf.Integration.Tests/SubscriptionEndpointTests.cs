using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Wf.ControlPlane.Api.Subscriptions;
using Wf.ControlPlane.Registry;
using Wf.Integration.Tests.Execution;

namespace Wf.Integration.Tests;

public sealed class SubscriptionEndpointTests(RollupDatabase database) : IClassFixture<RollupDatabase>
{
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private sealed record Scope(string Workflow, string Type, Guid Group, Guid Schedule, Guid Series)
    {
        public string Id(string kind) => kind switch
        {
            "workflow" => Workflow, "type" => Type, "schedule" => Schedule.ToString("D"),
            _ => throw new ArgumentException("Unknown fixture scope.")
        };
    }

    [Fact]
    public async Task Creation_receipt_and_persisted_list_use_the_same_PostgreSQL_microsecond_timestamp()
    {
        var scope = await Registry();
        var clock = new SubscriptionClock();
        await using var api = await MetricsApiServer.Start(database.DataSource, Token, timeProvider: clock);
        var jwt = api.Jwt(scope.Group);
        var row = await Create(api, jwt, Request(scope));
        var listed = Assert.Single((await List(api, jwt)).Items);
        Assert.Equal(clock.GetUtcNow().AddTicks(-7), row.CreatedAt);
        Assert.Equal(0, row.CreatedAt.Ticks % 10);
        Assert.Equal(row.CreatedAt, listed.CreatedAt);
    }

    private sealed class SubscriptionClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            new DateTimeOffset(2026, 10, 17, 9, 0, 0, TimeSpan.Zero).AddTicks(1234567);
    }

    [Theory]
    [InlineData("workflow", "Webhook")]
    [InlineData("schedule", "Email")]
    [InlineData("type", "Teams")]
    public async Task Actual_create_list_delete_are_scoped_owned_and_audited_without_destination_or_secret_leakage(string kind, string channel)
    {
        var scope = await Registry();
        await using var api = await MetricsApiServer.Start(database.DataSource, Token);
        var jwt = api.Jwt(scope.Group);
        var request = Request(scope, kind, channel);
        var row = await Create(api, jwt, request);
        Assert.Equal((kind, scope.Id(kind), channel), (row.Scope, row.ScopeId, row.Channel));
        Assert.Equal(["Completed", "Failed"], row.Events);
        Assert.Equal(request.Target, row.Target);
        Assert.Equal(request.SecretRef, row.SecretRef);
        var page = await List(api, jwt, "?scope=" + kind + "&scopeId=" + Uri.EscapeDataString(scope.Id(kind)));
        Assert.Equal(row.Id, Assert.Single(page.Items).Id);
        Assert.Null(page.NextCursor);
        Assert.Equal(1, await Count("SELECT count(*) FROM subscriptions WHERE id=$1 AND type_scope=$2 AND (series_id=$3 OR series_id IS NULL AND $3::uuid IS NULL)",
            row.Id, scope.Type, kind == "schedule" ? scope.Series : new Npgsql.NpgsqlParameter
                { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid, Value = DBNull.Value }));
        using var deleted = await api.Send(HttpMethod.Delete, "/api/v1/subscriptions/" + row.Id, jwt, null, Token);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Empty((await List(api, jwt)).Items);
        await using var audit = database.DataSource.CreateCommand(
            "SELECT action,summary::text FROM audit_log WHERE target_kind='Subscription' AND target_id=$1 ORDER BY id");
        audit.Parameters.AddWithValue(row.Id.ToString("D"));
        await using var reader = await audit.ExecuteReaderAsync(Token);
        foreach (var action in new[] { "subscription.create", "subscription.delete" })
        {
            Assert.True(await reader.ReadAsync(Token));
            Assert.Equal(action, reader.GetString(0));
            Assert.DoesNotContain(request.Target, reader.GetString(1));
            if (request.SecretRef is not null) Assert.DoesNotContain(request.SecretRef, reader.GetString(1));
        }
        Assert.False(await reader.ReadAsync(Token));
    }

    [Fact]
    public async Task Owner_is_from_authenticated_identity_and_other_users_or_global_Admin_cannot_read_or_delete_it()
    {
        var scope = await Registry();
        await using var api = await MetricsApiServer.Start(database.DataSource, Token);
        var owner = api.Jwt(scope.Group);
        var row = await Create(api, owner, Request(scope));
        foreach (var other in new[] { api.Jwt(scope.Group), api.Jwt(role: "Admin") })
        {
            Assert.Empty((await List(api, other)).Items);
            using var missing = await api.Send(HttpMethod.Delete, "/api/v1/subscriptions/" + row.Id, other, null, Token);
            await Problem(missing, 404, "subscription_not_found");
        }
        Assert.Equal(row.Id, Assert.Single((await List(api, owner)).Items).Id);
    }

    [Fact]
    public async Task Fresh_role_revocation_hides_existing_rows_and_prevents_creation_but_owner_can_still_unsubscribe()
    {
        var scope = await Registry();
        await using var api = await MetricsApiServer.Start(database.DataSource, Token);
        var jwt = api.Jwt(scope.Group);
        var row = await Create(api, jwt, Request(scope));
        await Execute("DELETE FROM role_bindings WHERE principal_id=$1", scope.Group);
        Assert.Empty((await List(api, jwt)).Items);
        using (var scoped = await api.Get("/api/v1/subscriptions?scope=workflow&scopeId=" + Uri.EscapeDataString(scope.Workflow), jwt, Token))
            await Problem(scoped, 403, "insufficient_scope_role");
        using (var denied = await Post(api, jwt, Request(scope)))
            await Problem(denied, 403, "insufficient_scope_role");
        using var deleted = await api.Send(HttpMethod.Delete, "/api/v1/subscriptions/" + row.Id, jwt, null, Token);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
    }

    [Theory]
    [InlineData("workflow")]
    [InlineData("schedule")]
    [InlineData("type")]
    public async Task Foreign_and_unknown_scopes_share_denial_without_creating_rows(string kind)
    {
        var own = await Registry();
        var foreign = await Registry();
        await using var api = await MetricsApiServer.Start(database.DataSource, Token);
        var jwt = api.Jwt(own.Group);
        foreach (var id in new[] { foreign.Id(kind), kind == "schedule" ? Guid.NewGuid().ToString() : foreign.Id(kind) + "-missing" })
        {
            using var created = await Post(api, jwt, Request(foreign, kind) with { ScopeId = id });
            await Problem(created, 403, "insufficient_scope_role");
            using var listed = await api.Get("/api/v1/subscriptions?scope=" + kind + "&scopeId=" + Uri.EscapeDataString(id), jwt, Token);
            await Problem(listed, 403, "insufficient_scope_role");
        }
        Assert.Empty((await List(api, jwt)).Items);
    }

    [Fact]
    public async Task Anonymous_and_app_only_identities_cannot_manage_personal_subscriptions()
    {
        var scope = await Registry();
        await using var api = await MetricsApiServer.Start(database.DataSource, Token);
        using (var anonymous = await Post(api, null, Request(scope)))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            Assert.True(anonymous.Headers.CacheControl!.NoStore);
        }
        foreach (var jwt in new[] { api.Jwt(scope.Group, identity: "app"), api.Jwt(role: "Admin", identity: "app") })
        {
            using var list = await api.Get("/api/v1/subscriptions", jwt, Token);
            await Problem(list, 403, "insufficient_scope_role");
            using var create = await Post(api, jwt, Request(scope));
            await Problem(create, 403, "insufficient_scope_role");
            using var delete = await api.Send(HttpMethod.Delete, "/api/v1/subscriptions/" + Guid.NewGuid(), jwt, null, Token);
            await Problem(delete, 403, "insufficient_scope_role");
        }
    }

    [Fact]
    public async Task Owned_UUID_keyset_pages_are_bounded_descending_and_ignore_newer_inserts_without_duplicates()
    {
        var scope = await Registry();
        await using var api = await MetricsApiServer.Start(database.DataSource, Token);
        var jwt = api.Jwt(scope.Group);
        var expected = new List<Guid>();
        for (var index = 0; index < 5; index++)
            expected.Add((await Create(api, jwt, Request(scope, channel: "Email") with { Target = $"subscriber-{index}@example.test" })).Id);
        var first = await List(api, jwt, "?limit=2");
        Assert.Equal(2, first.Items.Length);
        Assert.NotNull(first.NextCursor);
        var newer = await Create(api, jwt, Request(scope, channel: "Email") with { Target = "newer@example.test" });
        var actual = first.Items.Select(row => row.Id).ToList();
        var cursor = first.NextCursor;
        while (cursor is not null)
        {
            var page = await List(api, jwt, "?limit=2&cursor=" + cursor);
            Assert.InRange(page.Items.Length, 1, 2);
            actual.AddRange(page.Items.Select(row => row.Id));
            cursor = page.NextCursor;
        }
        Assert.Equal(expected.OrderByDescending(id => id), actual);
        Assert.DoesNotContain(newer.Id, actual);
    }

    [Fact]
    public async Task Concurrent_equivalent_creations_commit_one_subscription_and_one_audit_record()
    {
        var scope = await Registry();
        await using var api = await MetricsApiServer.Start(database.DataSource, Token);
        var jwt = api.Jwt(scope.Group);
        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(index =>
            Post(api, jwt, Request(scope) with { Events = index % 2 == 0 ? ["Completed", "Failed"] : ["Failed", "Completed"] })));
        try
        {
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
            Assert.Equal(11, responses.Count(response => response.StatusCode == HttpStatusCode.Conflict));
            var row = Assert.Single((await List(api, jwt)).Items);
            Assert.Equal(1, await Count("SELECT count(*) FROM audit_log WHERE target_kind='Subscription' AND target_id=$1", row.Id.ToString("D")));
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Theory]
    [InlineData("subscription.create")]
    [InlineData("subscription.delete")]
    public async Task Audit_failure_rolls_back_subscription_and_queued_outbox_mutations_then_retry_succeeds(string action)
    {
        var scope = await Registry();
        await using var api = await MetricsApiServer.Start(database.DataSource, Token);
        var jwt = api.Jwt(scope.Group);
        var actor = Guid.Parse(new JwtSecurityTokenHandler().ReadJwtToken(jwt).Claims.Single(claim => claim.Type == "oid").Value);
        var row = action == "subscription.delete" ? await Create(api, jwt, Request(scope)) : null;
        if (row is not null) await Outbox(scope, row.Id);
        var name = "reject_subscription_audit_" + Guid.NewGuid().ToString("N");
        await Execute($"""
            CREATE FUNCTION {name}() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'injected subscription audit failure'; END $$;
            CREATE TRIGGER {name} BEFORE INSERT ON audit_log FOR EACH ROW
                WHEN (NEW.actor='{actor:D}' AND NEW.action='{action}') EXECUTE FUNCTION {name}();
            """);
        try
        {
            using var failed = row is null ? await Post(api, jwt, Request(scope)) :
                await api.Send(HttpMethod.Delete, "/api/v1/subscriptions/" + row.Id, jwt, null, Token);
            await Problem(failed, 503, "subscriptions_unavailable");
            Assert.DoesNotContain("injected", await failed.Content.ReadAsStringAsync(Token));
            Assert.Equal(row is null ? 0 : 1, await Count("SELECT count(*) FROM subscriptions WHERE principal_id=$1", actor));
            if (row is not null)
                Assert.Equal(1, await Count("SELECT count(*) FROM notification_outbox WHERE subscription_id=$1", row.Id));
        }
        finally { await Execute($"DROP TRIGGER {name} ON audit_log; DROP FUNCTION {name}();"); }
        if (row is null) await Create(api, jwt, Request(scope));
        else
        {
            using var deleted = await api.Send(HttpMethod.Delete, "/api/v1/subscriptions/" + row.Id, jwt, null, Token);
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
            Assert.Equal(0, await Count("SELECT count(*) FROM notification_outbox WHERE subscription_id=$1", row.Id));
        }
    }

    [Theory]
    [InlineData("""{"scope":"invalid","scopeId":"t/y/w","events":["Completed"],"channel":"Email","target":"one@example.test"}""")]
    [InlineData("""{"scope":"type","scopeId":"t/y","events":["Completed","Completed"],"channel":"Email","target":"one@example.test"}""")]
    [InlineData("""{"scope":"type","scopeId":"t/y","events":["Completed"],"channel":"Email","target":"one@example.test","principalId":"spoofed"}""")]
    [InlineData("""{"scope":"type","scope":"workflow","scopeId":"t/y","events":["Completed"],"channel":"Email","target":"one@example.test"}""")]
    [InlineData("""{"scope":"type","scopeId":"t/y","events":null,"channel":"Email","target":"one@example.test"}""")]
    [InlineData("""{"scope":"type","scopeId":"t/y","channel":"Email","target":"one@example.test"}""")]
    [InlineData("null")]
    [InlineData("{")]
    public async Task Malformed_unknown_duplicate_null_and_impersonation_fields_fail_before_mutation(string json)
    {
        var scope = await Registry();
        await using var api = await MetricsApiServer.Start(database.DataSource, Token);
        var jwt = api.Jwt(scope.Group);
        using var invalid = await api.Send(HttpMethod.Post, "/api/v1/subscriptions", jwt, Json(json), Token);
        await Problem(invalid, 400, "invalid_subscription");
        Assert.Empty((await List(api, jwt)).Items);
    }

    [Fact]
    public async Task Both_declared_and_streamed_JSON_limits_and_media_type_are_enforced_by_real_Kestrel()
    {
        var scope = await Registry();
        await using var api = await MetricsApiServer.Start(database.DataSource, Token);
        var jwt = api.Jwt(scope.Group);
        var json = JsonSerializer.Serialize(Request(scope), SubscriptionJson.Default.SubscriptionRequest) + new string(' ', 20000);
        using (var declared = await api.Send(HttpMethod.Post, "/api/v1/subscriptions", jwt, Json(json), Token))
            await Problem(declared, 413, "subscription_too_large");
        var chunked = new ChunkedJsonContent(Encoding.UTF8.GetBytes(json));
        chunked.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using (var streamed = await api.Send(HttpMethod.Post, "/api/v1/subscriptions", jwt, chunked, Token))
            await Problem(streamed, 413, "subscription_too_large");
        using var wrongType = await api.Send(HttpMethod.Post, "/api/v1/subscriptions", jwt, new StringContent("{}"), Token);
        await Problem(wrongType, 415, "unsupported_subscription_content");
        Assert.Empty((await List(api, jwt)).Items);
    }

    [Theory]
    [InlineData("?scope=type")]
    [InlineData("?scopeId=t/y")]
    [InlineData("?scope=unknown&scopeId=t/y")]
    [InlineData("?limit=01")]
    [InlineData("?limit=101")]
    [InlineData("?limit=1&limit=2")]
    [InlineData("?principalId=other-user")]
    [InlineData("?cursor=00000000-0000-0000-0000-000000000000")]
    public async Task Invalid_list_filters_are_explicit_errors(string suffix)
    {
        var scope = await Registry();
        await using var api = await MetricsApiServer.Start(database.DataSource, Token);
        using var invalid = await api.Get("/api/v1/subscriptions" + suffix, api.Jwt(scope.Group), Token);
        await Problem(invalid, 400, "invalid_subscription");
    }

    [Fact]
    public async Task Storage_failure_is_sanitized_and_the_pool_recovers_after_schema_restoration()
    {
        var scope = await Registry();
        await using var api = await MetricsApiServer.Start(database.DataSource, Token);
        var jwt = api.Jwt(scope.Group);
        await Create(api, jwt, Request(scope));
        await Execute("ALTER TABLE subscriptions RENAME COLUMN target TO unavailable_subscription_target");
        try
        {
            using var failed = await api.Get("/api/v1/subscriptions", jwt, Token);
            await Problem(failed, 503, "subscriptions_unavailable");
            var body = await failed.Content.ReadAsStringAsync(Token);
            Assert.DoesNotContain("unavailable_subscription_target", body);
            Assert.DoesNotContain("Npgsql", body);
        }
        finally { await Execute("ALTER TABLE subscriptions RENAME COLUMN unavailable_subscription_target TO target"); }
        Assert.Single((await List(api, jwt)).Items);
    }

    [Theory]
    [InlineData("attempts=-1")]
    [InlineData("payload='[]'::jsonb")]
    [InlineData("delivered_at=now(),failed_at=now()")]
    [InlineData("last_error=repeat('x',257)")]
    public async Task Outbox_rejects_invalid_persisted_retry_payload_and_terminal_states(string mutation)
    {
        var scope = await Registry();
        await using var api = await MetricsApiServer.Start(database.DataSource, Token);
        var row = await Create(api, api.Jwt(scope.Group), Request(scope));
        var id = await Outbox(scope, row.Id);
        await using var invalid = database.DataSource.CreateCommand($"UPDATE notification_outbox SET {mutation} WHERE id=$1");
        invalid.Parameters.AddWithValue(id);
        var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => invalid.ExecuteNonQueryAsync(Token));
        Assert.Equal(Npgsql.PostgresErrorCodes.CheckViolation, error.SqlState);
    }

    [Fact]
    public async Task Single_connection_pool_can_authorize_create_list_and_delete_without_nested_connection_waits()
    {
        var scope = await Registry();
        await using var source = database.SingleConnection();
        await using var api = await MetricsApiServer.Start(source, Token);
        var jwt = api.Jwt(scope.Group);
        var row = await Create(api, jwt, Request(scope, "schedule"));
        Assert.Equal(row.Id, Assert.Single((await List(api, jwt,
            "?scope=schedule&scopeId=" + scope.Schedule)).Items).Id);
        using var deleted = await api.Send(HttpMethod.Delete, "/api/v1/subscriptions/" + row.Id, jwt, null, Token);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Empty((await List(api, jwt)).Items);
    }

    [Theory]
    [InlineData("events=ARRAY['Completed','Completed']")]
    [InlineData("events='{}'::text[]")]
    [InlineData("events=ARRAY['Unknown']")]
    [InlineData("events=ARRAY[NULL::text]")]
    [InlineData("scope_kind='unknown'")]
    [InlineData("secret_ref=NULL")]
    [InlineData("scope_kind='schedule'")]
    [InlineData("principal_id='00000000-0000-0000-0000-000000000000'")]
    [InlineData("scope_id='other/type/workflow'")]
    [InlineData("target=E'line\\nbreak'")]
    public async Task Persisted_subscription_guards_reject_invalid_events_scope_ownership_and_destination_shapes(string mutation)
    {
        var scope = await Registry();
        await using var api = await MetricsApiServer.Start(database.DataSource, Token);
        var row = await Create(api, api.Jwt(scope.Group), Request(scope));
        await using var invalid = database.DataSource.CreateCommand($"UPDATE subscriptions SET {mutation} WHERE id=$1");
        invalid.Parameters.AddWithValue(row.Id);
        var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => invalid.ExecuteNonQueryAsync(Token));
        Assert.Equal(Npgsql.PostgresErrorCodes.CheckViolation, error.SqlState);
    }

    [Fact]
    public async Task Outbox_uniqueness_prevents_duplicate_run_deliveries_and_run_removal_cascades_without_deleting_subscription()
    {
        var scope = await Registry();
        await using var api = await MetricsApiServer.Start(database.DataSource, Token);
        var jwt = api.Jwt(scope.Group);
        var row = await Create(api, jwt, Request(scope));
        var id = await Outbox(scope, row.Id);
        await using (var duplicate = database.DataSource.CreateCommand("""
            INSERT INTO notification_outbox(id,subscription_id,run_day,run_id,payload)
            SELECT $1,subscription_id,run_day,run_id,payload FROM notification_outbox WHERE id=$2
            """))
        {
            duplicate.Parameters.AddWithValue(Guid.CreateVersion7());
            duplicate.Parameters.AddWithValue(id);
            var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => duplicate.ExecuteNonQueryAsync(Token));
            Assert.Equal(Npgsql.PostgresErrorCodes.UniqueViolation, error.SqlState);
        }
        await Execute("DELETE FROM runs WHERE id=(SELECT run_id FROM notification_outbox WHERE id=$1)", id);
        Assert.Equal(0, await Count("SELECT count(*) FROM notification_outbox WHERE subscription_id=$1", row.Id));
        Assert.Equal(row.Id, Assert.Single((await List(api, jwt)).Items).Id);
    }

    private static SubscriptionRequest Request(Scope scope, string kind = "workflow", string channel = "Webhook") =>
        new(kind, scope.Id(kind), ["Failed", "Completed"], channel, channel switch
        {
            "Webhook" => "https://notify.example.test/hook",
            "Email" => "subscriber@example.test",
            "Teams" => "secret://notifications/teams-channel",
            _ => throw new ArgumentException("Unknown fixture channel.")
        }, channel == "Webhook" ? "secret://notifications/signing" : null);

    private async Task<Scope> Registry()
    {
        var team = "subscription-" + Guid.NewGuid().ToString("N");
        var scope = new Scope(team + "/notifications/workflow", team + "/notifications", Guid.NewGuid(), Guid.CreateVersion7(), Guid.CreateVersion7());
        await using var connection = await database.DataSource.OpenConnectionAsync(Token);
        await using var transaction = await connection.BeginTransactionAsync(Token);
        await new RegistryWriter().UpsertAsync(connection, transaction, new(team,
            [new(team, "notifications", "Subscription integration", new([]))],
            RoleBindings: [new(Contracts.Registry.RegistryRole.Viewer, scope.Group)]), Token);
        await using var query = new Npgsql.NpgsqlCommand("""
            WITH workflow AS (
                INSERT INTO workflows(id,type_id) VALUES($1,$2) RETURNING id
            ), schedule AS (
                INSERT INTO schedules(id,workflow_id,origin,name,timezone,interval_s,created_by,updated_by)
                SELECT $3,id,1,'Notifications','UTC',60,'test','test' FROM workflow RETURNING id,workflow_id
            )
            INSERT INTO run_series(id,workflow_id,schedule_id) SELECT $4,workflow_id,id FROM schedule
            """, connection);
        query.Parameters.AddWithValue(scope.Workflow);
        query.Parameters.AddWithValue(scope.Type);
        query.Parameters.AddWithValue(scope.Schedule);
        query.Parameters.AddWithValue(scope.Series);
        await query.ExecuteNonQueryAsync(Token);
        await transaction.CommitAsync(Token);
        return scope;
    }

    private async Task<Guid> Outbox(Scope scope, Guid subscription)
    {
        var run = Guid.CreateVersion7();
        var id = Guid.CreateVersion7();
        await Execute("SELECT ensure_run_partitions(uuid_v7_run_day($1))", run);
        await Execute("""
            WITH run AS (
                INSERT INTO runs(run_day,id,workflow_id,version_id,trigger,triggered_by,variables,metadata,status,created_at,deadline_at)
                VALUES(uuid_v7_run_day($1),$1,$2,$1,0,'subscription-test','{}','{}',3,now(),now()+interval '1 day')
                RETURNING run_day,id
            )
            INSERT INTO notification_outbox(id,subscription_id,run_day,run_id,payload)
                SELECT $3,$4,run_day,id,'{}' FROM run
            """, run, scope.Workflow, id, subscription);
        return id;
    }

    private Task<HttpResponseMessage> Post(MetricsApiServer api, string? jwt, SubscriptionRequest request) =>
        api.Send(HttpMethod.Post, "/api/v1/subscriptions", jwt,
            Json(JsonSerializer.Serialize(request, SubscriptionJson.Default.SubscriptionRequest)), Token);
    private async Task<SubscriptionRow> Create(MetricsApiServer api, string jwt, SubscriptionRequest request)
    {
        using var response = await Post(api, jwt, request);
        var body = await response.Content.ReadAsStringAsync(Token);
        Assert.True(response.StatusCode == HttpStatusCode.Created, body);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var row = JsonSerializer.Deserialize(body, SubscriptionJson.Default.SubscriptionRow)!;
        Assert.Equal("/api/v1/subscriptions/" + row.Id, response.Headers.Location!.ToString());
        return row;
    }
    private async Task<SubscriptionPage> List(MetricsApiServer api, string jwt, string suffix = "")
    {
        using var response = await api.Get("/api/v1/subscriptions" + suffix, jwt, Token);
        var body = await response.Content.ReadAsStringAsync(Token);
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        Assert.True(response.Headers.CacheControl!.NoStore);
        return JsonSerializer.Deserialize(body, SubscriptionJson.Default.SubscriptionPage)!;
    }
    private async Task Problem(HttpResponseMessage response, int status, string reason)
    {
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        Assert.Equal(reason, body.RootElement.GetProperty("reason").GetString());
    }
    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");
    private async Task Execute(string sql, params object[] values)
    {
        await using var query = database.DataSource.CreateCommand(sql);
        Add(query, values);
        await query.ExecuteNonQueryAsync(Token);
    }
    private async Task<long> Count(string sql, params object[] values)
    {
        await using var query = database.DataSource.CreateCommand(sql);
        Add(query, values);
        return (long)(await query.ExecuteScalarAsync(Token))!;
    }
    private static void Add(Npgsql.NpgsqlCommand query, object[] values)
    {
        foreach (var value in values)
            if (value is Npgsql.NpgsqlParameter parameter) query.Parameters.Add(parameter);
            else query.Parameters.AddWithValue(value);
    }
    private sealed class ChunkedJsonContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
}
