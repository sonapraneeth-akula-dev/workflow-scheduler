using System.Collections.Immutable;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Contracts.Events;
using Wf.Contracts.Registry;
using Wf.ControlPlane.Api;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Leadership;
using Wf.ControlPlane.Engine.Recovery;
using Wf.ControlPlane.Registry;
using Wf.Engine.Core;
using Wf.Testing.RunQueries;

namespace Wf.Integration.Tests.Auth;

public sealed class RunAbortTests(AuthorizationDatabase database) : IClassFixture<AuthorizationDatabase>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private RunStore Store => new(database.DataSource, NullLogger<RunStore>.Instance);
    private Task<RunQueryServer> Server() => RunQueryServer.Start(database.DataSource, Token, abort: true);

    [Theory]
    [InlineData(RegistryRole.Viewer, false, false)]
    [InlineData(RegistryRole.Author, false, false)]
    [InlineData(RegistryRole.Operator, false, true)]
    [InlineData(RegistryRole.Admin, false, true)]
    [InlineData(RegistryRole.Viewer, true, false)]
    [InlineData(RegistryRole.Author, true, false)]
    [InlineData(RegistryRole.Operator, true, true)]
    [InlineData(RegistryRole.Admin, true, true)]
    public async Task RunAbort_requires_fresh_operator_or_admin_on_the_stored_run_scope(
        RegistryRole role, bool teamScope, bool allowed)
    {
        var seed = await Seed(role, teamScope);
        await using var server = await Server();
        var jwt = server.Jwt(seed.Group, extra: [new Claim("name", "Test operator")]);
        using var response = await Abort(server, seed.Run, jwt);
        Assert.Equal(allowed ? HttpStatusCode.Accepted : HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal(allowed ? 1L : 0L, await Count(seed.Run, "commands"));
        Assert.Equal(allowed ? 1L : 0L, await Count(seed.Run, "audit_log"));
        using var foreign = await Abort(server, seed.OtherRun, jwt);
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        Assert.Equal(0L, await Count(seed.OtherRun, "commands"));
        if (!allowed) return;
        var sibling = await Create(seed.Team + "/sibling/workflow");
        using var siblingResponse = await Abort(server, sibling, jwt);
        Assert.Equal(teamScope ? HttpStatusCode.Accepted : HttpStatusCode.Forbidden, siblingResponse.StatusCode);
        await Execute("DELETE FROM role_bindings WHERE principal_id=$1", seed.Group);
        using var revoked = await Abort(server, seed.Run, jwt);
        Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
    }

    [Theory]
    [InlineData(null, "user", "api.read", "")]
    [InlineData("foreign", "user", "api.read", "")]
    [InlineData("own", "app", null, "Registrar")]
    [InlineData("own", "app", null, "Agent")]
    [InlineData("own", "app", null, "")]
    public async Task RunAbort_denies_anonymous_foreign_and_workload_callers(string? group, string identity, string? scope, string role)
    {
        var seed = await Seed();
        await using var server = await Server();
        var jwt = group is null ? null : server.Jwt(group == "own" ? seed.Group : Guid.NewGuid(), identity, scope, role);
        using var response = await Abort(server, seed.Run, jwt);
        Assert.Equal(group is null || scope is null && role.Length == 0
            ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0L, await Count(seed.Run, "commands"));
        Assert.Equal(0L, await Count(seed.Run, "audit_log"));
        using var unknown = await Abort(server, Guid.CreateVersion7(), server.Jwt(seed.Group));
        Assert.Equal(HttpStatusCode.Forbidden, unknown.StatusCode);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("""{"reason":null}""")]
    [InlineData("""{"reason":""}""")]
    [InlineData("""{"reason":" \t\r\n "}""")]
    [InlineData("""{"reason":5}""")]
    [InlineData("""{"reason":"one","reason":"two"}""")]
    [InlineData("""{"reason":"stop","by":"spoofed"}""")]
    [InlineData("""{"reason":"stop","source":"KillSwitch"}""")]
    [InlineData("""{"reason":"stop","source":"Local"}""")]
    [InlineData("""{"reason":"stop","source":"invalid"}""")]
    [InlineData("""{"reason":"stop","source":100}""")]
    [InlineData("""{"reason":"unterminated""")]
    public async Task RunAbort_rejects_invalid_or_spoofed_contract_without_writes_or_value_leakage(string body)
    {
        var seed = await Seed();
        await using var server = await Server();
        using var response = await Abort(server, seed.Run, server.Jwt(seed.Group), body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var text = await response.Content.ReadAsStringAsync(Token);
        Assert.DoesNotContain("spoofed", text);
        Assert.DoesNotContain("unterminated", text);
        Assert.Equal(0L, await Count(seed.Run, "commands"));
        Assert.Equal(0L, await Count(seed.Run, "audit_log"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAbort_enforces_reason_and_streamed_body_limits(bool chunked)
    {
        var seed = await Seed();
        await using var server = await Server();
        var jwt = server.Jwt(seed.Group);
        using (var tooLong = await Abort(server, seed.Run, jwt,
            JsonSerializer.Serialize(new { reason = new string('x', 501) })))
            Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        var oversized = Encoding.UTF8.GetBytes("""{"reason":"stop"}""" + new string(' ', 17 * 1024));
        using (HttpContent content = chunked ? new ChunkedContent(oversized) : new ByteArrayContent(oversized))
        {
            content.Headers.ContentType = new("application/json");
            using var response = await server.Post("/api/v1/runs/" + seed.Run + "/abort", jwt, content);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        }
        using (var plain = await server.Post("/api/v1/runs/" + seed.Run + "/abort", jwt, new StringContent("stop")))
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, plain.StatusCode);
        Assert.Equal(0L, await Count(seed.Run, "commands"));
        using var valid = await Abort(server, seed.Run, jwt, JsonSerializer.Serialize(new { reason = new string('x', 500) }));
        Assert.Equal(HttpStatusCode.Accepted, valid.StatusCode);
        Assert.Equal(500, (await Record(valid)).Reason.Length);
    }

    [Theory]
    [InlineData("UI")]
    [InlineData("API")]
    [InlineData("CLI")]
    public async Task RunAbort_first_acceptance_identity_timestamp_source_and_audit_are_immutable(string source)
    {
        var seed = await Seed();
        await using var server = await Server();
        var jwt = server.Jwt(seed.Group, extra: [new Claim("name", "First operator")]);
        var actor = new JwtSecurityTokenHandler().ReadJwtToken(jwt).Claims.Single(claim => claim.Type == "oid").Value;
        await using var listener = await database.DataSource.OpenConnectionAsync(Token);
        var notifications = new List<string>();
        listener.Notification += (_, message) => notifications.Add(message.Payload);
        await using (var listen = new NpgsqlCommand("LISTEN commands", listener)) await listen.ExecuteNonQueryAsync(Token);
        var before = DateTimeOffset.UtcNow;
        using var response = await Abort(server, seed.Run, jwt, JsonSerializer.Serialize(new { reason = "Original reason", source }));
        var record = await Record(response);
        Assert.Equal(actor, record.By);
        Assert.Equal("First operator", record.ByName);
        Assert.Equal("Original reason", record.Reason);
        Assert.Equal(source, record.Source.ToString());
        Assert.InRange(record.At, before, DateTimeOffset.UtcNow);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (!notifications.Contains(seed.Run.ToString("D"))) await listener.WaitAsync(timeout.Token);
        using var repeat = await Abort(server, seed.Run, server.Jwt(seed.Group, extra: [new Claim("name", "Other operator")]),
            """{"reason":"Second reason","source":"CLI"}""");
        Assert.Equal(record, await Record(repeat));
        Assert.Equal(1L, await Count(seed.Run, "commands"));
        Assert.Equal(1L, await Count(seed.Run, "audit_log"));
        var audit = await JsonScalar("SELECT summary::text FROM audit_log WHERE target_id=$1", seed.Run.ToString("D"));
        Assert.Equal(record.By, audit.GetProperty("by").GetString());
        Assert.Equal(record.ByName, audit.GetProperty("byName").GetString());
        Assert.Equal(record.Reason, audit.GetProperty("reason").GetString());
        Assert.Equal(source, audit.GetProperty("source").GetString());
        Assert.Equal(record.At.Ticks / 10, audit.GetProperty("at").GetDateTimeOffset().Ticks / 10);
        Assert.Equal("User", await Scalar("SELECT actor_kind FROM audit_log WHERE target_id=$1", seed.Run.ToString("D")));
        Assert.Equal((short)RunStatus.Queued, await Scalar("SELECT status FROM runs WHERE id=$1", seed.Run));
        Assert.Equal(0L, await Scalar("SELECT count(*) FROM run_events WHERE run_id=$1", seed.Run));
        Assert.Null((await Store.LoadAsync(seed.Run, Token)).Snapshot.Abort);
    }

    [Theory]
    [InlineData(RunStatus.Succeeded)]
    [InlineData(RunStatus.Failed)]
    [InlineData(RunStatus.Aborted)]
    [InlineData(RunStatus.TimedOut)]
    public async Task RunAbort_rejects_every_terminal_status_with_409_and_no_mutation(RunStatus status)
    {
        var seed = await Seed(status: status);
        await using var server = await Server();
        using var response = await Abort(server, seed.Run, server.Jwt(seed.Group));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.Equal(409, problem.GetProperty("status").GetInt32());
        Assert.Equal(status.ToString(), problem.GetProperty("runStatus").GetString());
        Assert.Equal(problem.EnumerateObject().Count(), problem.EnumerateObject().Select(property => property.Name).Distinct().Count());
        Assert.Equal(0L, await Count(seed.Run, "commands"));
        Assert.Equal(0L, await Count(seed.Run, "audit_log"));
    }

    [Fact]
    public async Task RunAbort_concurrent_callers_and_hosts_return_one_original_record_and_one_audit()
    {
        var seed = await Seed();
        await using var first = await Server();
        await using var second = await Server();
        var records = await Task.WhenAll(Enumerable.Range(0, 24).Select(async index =>
        {
            var server = index % 2 == 0 ? first : second;
            using var response = await Abort(server, seed.Run, server.Jwt(seed.Group),
                JsonSerializer.Serialize(new { reason = "reason-" + index }));
            return await Record(response);
        }));
        Assert.Single(records.Distinct());
        Assert.Equal(1L, await Count(seed.Run, "commands"));
        Assert.Equal(1L, await Count(seed.Run, "audit_log"));
    }

    [Fact]
    public async Task RunAbort_repeat_does_not_wait_for_recovery_command_row_lock()
    {
        var seed = await Seed();
        await using var server = await Server();
        using var initial = await Abort(server, seed.Run, server.Jwt(seed.Group));
        var record = await Record(initial);
        await using var connection = await database.DataSource.OpenConnectionAsync(Token);
        await using var transaction = await connection.BeginTransactionAsync(Token);
        await using var query = new NpgsqlCommand("SELECT id FROM commands WHERE run_id=$1 AND kind=1 FOR UPDATE", connection, transaction);
        query.Parameters.AddWithValue(seed.Run);
        Assert.IsType<Guid>(await query.ExecuteScalarAsync(Token));
        using var repeat = await Abort(server, seed.Run, server.Jwt(seed.Group)).WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal(record, await Record(repeat));
    }

    [Fact]
    public async Task RunAbort_audit_failure_rolls_back_command_and_notification_then_allows_retry()
    {
        var seed = await Seed();
        await using var server = await Server();
        await using var listener = await database.DataSource.OpenConnectionAsync(Token);
        await using (var listen = new NpgsqlCommand("LISTEN commands", listener)) await listen.ExecuteNonQueryAsync(Token);
        await Execute($$"""
            CREATE FUNCTION reject_abort_audit() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN IF NEW.target_id='{{seed.Run:D}}' THEN RAISE EXCEPTION 'intentional audit failure'; END IF; RETURN NEW; END $$;
            CREATE TRIGGER reject_abort_audit BEFORE INSERT ON audit_log FOR EACH ROW EXECUTE FUNCTION reject_abort_audit()
            """);
        try
        {
            using var response = await Abort(server, seed.Run, server.Jwt(seed.Group));
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.Equal(0L, await Count(seed.Run, "commands"));
            Assert.Equal(0L, await Count(seed.Run, "audit_log"));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(200));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listener.WaitAsync(timeout.Token));
        }
        finally { await Execute("DROP TRIGGER reject_abort_audit ON audit_log; DROP FUNCTION reject_abort_audit()"); }
        using var retry = await Abort(server, seed.Run, server.Jwt(seed.Group));
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
    }

    [Fact]
    public async Task RunAbort_waiting_for_a_terminal_commit_returns_409_instead_of_accepting_a_late_command()
    {
        var seed = await Seed(status: RunStatus.Running);
        await using var server = await Server();
        await using var connection = await database.DataSource.OpenConnectionAsync(Token);
        await using var transaction = await connection.BeginTransactionAsync(Token);
        await using (var update = new NpgsqlCommand("UPDATE runs SET status=3,ended_at=clock_timestamp() WHERE id=$1", connection, transaction))
        {
            update.Parameters.AddWithValue(seed.Run);
            Assert.Equal(1, await update.ExecuteNonQueryAsync(Token));
        }
        var pending = Abort(server, seed.Run, server.Jwt(seed.Group));
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (await Scalar("""
            SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE $1=ANY(pg_blocking_pids(pid))
                AND query LIKE '%SELECT status,workflow_id FROM runs%')
            """, connection.ProcessID) is not true)
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "The real HTTP abort never waited on the held run row.");
            await Task.Delay(25, Token);
        }
        await transaction.CommitAsync(Token);
        using var response = await pending.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(0L, await Count(seed.Run, "commands"));
        Assert.Equal(0L, await Count(seed.Run, "audit_log"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAbort_real_recovery_applies_accepted_and_unmaterialized_trigger_records_once(bool pendingTrigger)
    {
        var seed = await Seed(status: RunStatus.Running);
        var id = seed.Run;
        if (pendingTrigger)
        {
            var initial = NewRun();
            id = initial.Snapshot.RunId;
            await new CommandInbox(database.DataSource, TimeProvider.System).TriggerAsync(
                new(initial.Snapshot, seed.Team + "/release/workflow", Guid.CreateVersion7(), DateTimeOffset.UtcNow.AddHours(1)),
                "triggering-user", Token);
        }
        await using var server = await Server();
        var jwt = server.Jwt(seed.Group);
        using var accepted = await Abort(server, id, jwt);
        var record = await Record(accepted);
        using var leader = new LeaseManager(database.DataSource,
            new(Guid.NewGuid().ToString("N"), new Uri("http://localhost:5200")), NullLogger<LeaseManager>.Instance);
        await Execute("UPDATE leases SET expires_at=clock_timestamp()-interval '1 second'");
        Assert.True(await leader.TryAcquireOrRenewAsync(Token));
        using var recovery = new RunRecovery(database.DataSource, Store, leader, TimeProvider.System, NullLogger<RunRecovery>.Instance);
        await recovery.RecoverAsync(Token);
        var loaded = await Store.LoadAsync(id, Token);
        Assert.Equal(pendingTrigger ? RunStatus.Aborted : RunStatus.Aborting, loaded.Snapshot.Status);
        Assert.Equal(new AbortRecord(record.By, record.ByName, record.At, record.Reason, record.Source), loaded.Snapshot.Abort);
        var sequence = loaded.Snapshot.LastSequence;
        await recovery.RecoverAsync(Token);
        Assert.Equal(sequence, (await Store.LoadAsync(id, Token)).Snapshot.LastSequence);
        using var repeat = await Abort(server, id, jwt);
        if (pendingTrigger) Assert.Equal(HttpStatusCode.Conflict, repeat.StatusCode);
        else Assert.Equal(record, await Record(repeat));
        Assert.Equal(1L, await Count(id, "audit_log"));
    }

    [Fact]
    public async Task RunAbort_global_admin_and_openapi_expose_the_public_contract_without_spa_restrictions()
    {
        var seed = await Seed(RegistryRole.Viewer);
        await using var server = await Server();
        var jwt = server.Jwt(identity: "app", scope: null, role: "Admin");
        using var response = await Abort(server, seed.Run, jwt);
        var record = await Record(response);
        Assert.Equal(record.By, record.ByName);
        Assert.Equal("App", await Scalar("SELECT actor_kind FROM audit_log WHERE target_id=$1", seed.Run.ToString("D")));
        using var schemaResponse = await server.Get("/openapi/v1.json", jwt);
        Assert.Equal(HttpStatusCode.OK, schemaResponse.StatusCode);
        var schema = await schemaResponse.Content.ReadFromJsonAsync<JsonElement>(Token);
        var endpoint = schema.GetProperty("paths").GetProperty("/api/v1/runs/{id}/abort").GetProperty("post");
        Assert.True(endpoint.GetProperty("responses").TryGetProperty("202", out _));
        Assert.True(endpoint.TryGetProperty("requestBody", out _));
    }

    private async Task<(string Team, Guid Group, Guid Run, Guid OtherRun)> Seed(
        RegistryRole role = RegistryRole.Operator, bool teamScope = false, RunStatus status = RunStatus.Queued)
    {
        var team = "runabort-" + Guid.NewGuid().ToString("N");
        var group = Guid.NewGuid();
        var binding = new RegistryRoleBinding(role, group);
        await using var connection = await database.DataSource.OpenConnectionAsync(Token);
        await using var transaction = await connection.BeginTransactionAsync(Token);
        await new RegistryWriter().UpsertAsync(connection, transaction,
            new(team, [new(team, "release", "description", new([]), teamScope ? [] : [binding]),
                new(team, "sibling", "description", new([]))], RoleBindings: teamScope ? [binding] : []), Token);
        await new RegistryWriter().UpsertAsync(connection, transaction,
            new(team + "-foreign", [new(team + "-foreign", "release", "description", new([]))]), Token);
        await transaction.CommitAsync(Token);
        return (team, group, await Create(team + "/release/workflow", status), await Create(team + "-foreign/release/workflow"));
    }

    private static RunState NewRun() => new(Guid.CreateVersion7(),
        [new NodeDefinition("node", [], [new TaskDefinition("task")])],
        new WorkflowMetadata("release", ImmutableSortedDictionary<string, string>.Empty), Assert.Fail);

    private async Task<Guid> Create(string workflow, RunStatus status = RunStatus.Queued)
    {
        var run = NewRun();
        var store = Store;
        await store.CreateAsync(run, workflow, Guid.CreateVersion7(), DateTimeOffset.UtcNow.AddHours(1), Token);
        if (status == RunStatus.Aborted)
            await store.AppendAsync(run, EventEntity.Run, null, (int)status, DateTimeOffset.UtcNow, Token);
        else if (status != RunStatus.Queued)
        {
            await store.AppendAsync(run, EventEntity.Run, null, (int)RunStatus.Running, DateTimeOffset.UtcNow, Token);
            if (status != RunStatus.Running)
                await store.AppendAsync(run, EventEntity.Run, null, (int)status, DateTimeOffset.UtcNow, Token);
        }
        return run.Snapshot.RunId;
    }

    private static Task<HttpResponseMessage> Abort(RunQueryServer server, Guid id, string? jwt,
        string body = """{"reason":"Stop this run"}""") =>
        server.Post("/api/v1/runs/" + id + "/abort", jwt, new StringContent(body, Encoding.UTF8, "application/json"));

    private static async Task<AbortRunResponse> Record(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        return (await response.Content.ReadFromJsonAsync(AbortRunJson.Default.AbortRunResponse, Token))!;
    }

    private async Task<long> Count(Guid id, string table) => (long)(await Scalar(table == "commands"
        ? "SELECT count(*) FROM commands WHERE run_id=$1 AND kind=1"
        : "SELECT count(*) FROM audit_log WHERE target_id=$1", table == "commands" ? id : id.ToString("D")))!;

    private async Task<object?> Scalar(string sql, params object[] values)
    {
        await using var query = database.DataSource.CreateCommand(sql);
        foreach (var value in values) query.Parameters.AddWithValue(value);
        return await query.ExecuteScalarAsync(Token);
    }

    private async Task<JsonElement> JsonScalar(string sql, params object[] values) =>
        JsonSerializer.Deserialize<JsonElement>((string)(await Scalar(sql, values))!);

    private async Task Execute(string sql, params object[] values)
    {
        await using var query = database.DataSource.CreateCommand(sql);
        foreach (var value in values) query.Parameters.AddWithValue(value);
        await query.ExecuteNonQueryAsync(Token);
    }

    private sealed class ChunkedContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(bytes.AsMemory()).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
}
