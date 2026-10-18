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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Microsoft.IdentityModel.Validators;
using Npgsql;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Events;
using Wf.Contracts.Registry;
using Wf.Contracts.Runs;
using Wf.ControlPlane.Api;
using Wf.ControlPlane.Auth;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Events;
using Wf.ControlPlane.Engine.Recovery;
using Wf.ControlPlane.Engine.Registry;
using Wf.ControlPlane.Live;
using Wf.ControlPlane.Registry;
using Wf.Engine.Core;
using Wf.Integration.Tests.Auth;

namespace Wf.Integration.Tests.Live;

public sealed class RunEventsResyncTests(AuthorizationDatabase database) : IClassFixture<AuthorizationDatabase>
{
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private RunStore Store => new(database.DataSource, NullLogger<RunStore>.Instance);

    [Fact]
    public async Task Progress_tail_uses_the_live_contract_without_changing_graph_state_or_exposing_remote_references()
    {
        var seed = await Seed();
        var at = DateTimeOffset.UtcNow;
        var changes = new[]
        {
            seed.State.Append(EventEntity.Run, null, (int)RunStatus.Running, at),
            seed.State.Append(EventEntity.Node, "node", (int)ExecutionStatus.Ready, at),
            seed.State.Append(EventEntity.Task, "node/task0", (int)ExecutionStatus.Ready, at),
            seed.State.Append(EventEntity.Task, "node/task0", (int)ExecutionStatus.Dispatched, at),
            seed.State.Append(EventEntity.Task, "node/task0", (int)ExecutionStatus.Running, at)
        };
        var occurrence = new DateTimeOffset(at.UtcTicks / 10 * 10, TimeSpan.Zero);
        var progress = new RunEvent(seed.Id, 6, at, EventEntity.Task, "node/task0", (int)ExecutionStatus.Running)
        { Attempt = 1, Progress = new(1, RemoteOperationPhase.Waiting, occurrence) };
        seed.State.Apply(progress);
        await Append([.. changes, progress]);
        await using var server = await Server.Start(database.DataSource);
        using var response = await server.Get(Events(seed.Id, 5), server.Jwt(seed.Group));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await Json(response);
        Assert.Equal(6, page.GetProperty("lastSeq").GetInt64());
        var change = Assert.Single(page.GetProperty("events").EnumerateArray());
        Assert.Equal(1, change.GetProperty("attempt").GetInt32());
        Assert.Equal(3, change.GetProperty("state").GetInt32());
        Assert.Equal(["occurredAt", "phase", "sequence"],
            change.GetProperty("progress").EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal((int)RemoteOperationPhase.Waiting, change.GetProperty("progress").GetProperty("phase").GetInt32());
        Assert.DoesNotContain("secret-sentinel", page.GetRawText());
        Assert.Equal(LiveEvent.From(progress),
            JsonSerializer.Deserialize(page, RunEventsJsonContext.Default.RunEventsPage)!.Events.Single());
        using var snapshot = await server.Get(Snapshot(seed.Id), server.Jwt(seed.Group));
        var graph = await Json(snapshot);
        Assert.Equal(6, graph.GetProperty("lastSeq").GetInt64());
        Assert.Equal(3, graph.GetProperty("tasks").GetProperty("node/task0").GetInt32());
        Assert.False(graph.TryGetProperty("progress", out _));
    }

    [Fact]
    public async Task Materialized_snapshot_plus_unsnapshotted_tail_restores_exact_graph_and_safe_live_wire()
    {
        var seed = await Seed();
        await using var server = await Server.Start(database.DataSource);
        await Store.AppendAsync(seed.State, EventEntity.Run, null, (int)RunStatus.Running, DateTimeOffset.UtcNow, Token);
        var at = DateTimeOffset.UtcNow;
        var tail = new[]
        {
            seed.State.Append(EventEntity.Node, "node", (int)ExecutionStatus.Ready, at),
            seed.State.Append(EventEntity.Task, "node/task0", (int)ExecutionStatus.Ready, at),
            seed.State.Append(EventEntity.Task, "node/task0", (int)ExecutionStatus.Dispatched, at),
            seed.State.Append(EventEntity.Task, "node/task0", (int)ExecutionStatus.Running, at)
        };
        await Append(tail);
        Assert.Equal(1L, await Scalar("SELECT snapshot_seq FROM runs WHERE id=$1", seed.Id));
        using var response = await server.Get(Events(seed.Id, 1), server.Jwt(seed.Group));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var page = await Json(response);
        Assert.Equal(seed.Id, page.GetProperty("runId").GetGuid());
        Assert.Equal(5, page.GetProperty("lastSeq").GetInt64());
        Assert.Equal(tail.Select(LiveEvent.From).ToArray(),
            JsonSerializer.Deserialize(page, RunEventsJsonContext.Default.RunEventsPage)!.Events);
        using var snapshotResponse = await server.Get(Snapshot(seed.Id), server.Jwt(seed.Group));
        var snapshot = await Json(snapshotResponse);
        Assert.Equal(HttpStatusCode.OK, snapshotResponse.StatusCode);
        Assert.True(snapshotResponse.Headers.CacheControl!.NoStore);
        Assert.Equal(seed.Workflow, snapshot.GetProperty("workflowId").GetString());
        Assert.Equal(seed.Version, snapshot.GetProperty("versionId").GetGuid());
        Assert.Equal(5, snapshot.GetProperty("lastSeq").GetInt64());
        Assert.Equal((int)RunStatus.Running, snapshot.GetProperty("status").GetInt32());
        Assert.Equal((int)ExecutionStatus.Ready, snapshot.GetProperty("nodes").GetProperty("node").GetInt32());
        Assert.Equal((int)ExecutionStatus.Running, snapshot.GetProperty("tasks").GetProperty("node/task0").GetInt32());
        Assert.DoesNotContain("secret-sentinel", snapshot.GetRawText());
        Assert.Equal(JsonValueKind.Null, snapshot.GetProperty("parentRunId").ValueKind);
        Assert.Empty(snapshot.GetProperty("reusedNodes").EnumerateObject());
        Assert.Equal(JsonValueKind.Null, snapshot.GetProperty("placement").ValueKind);
        Assert.Equal(JsonValueKind.Null, snapshot.GetProperty("placementAgentId").ValueKind);
        Assert.Empty(snapshot.GetProperty("taskWaitReasons").EnumerateObject());
        Assert.Equal(new[] { "abort", "endedAt", "lastSeq", "nodes", "parentRunId", "placement", "placementAgentId", "reusedNodes", "runId", "seriesId", "status", "taskWaitReasons", "tasks", "versionId", "workflowId" }.Order(StringComparer.Ordinal),
            snapshot.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        using var empty = await server.Get(Events(seed.Id, 5), server.Jwt(seed.Group));
        var emptyPage = await Json(empty);
        Assert.Equal(5, emptyPage.GetProperty("lastSeq").GetInt64());
        Assert.Empty(emptyPage.GetProperty("events").EnumerateArray());
    }

    [Fact]
    public async Task Exactly_5000_is_success_5001_is_409_never_a_truncated_success()
    {
        var seed = await Seed(1667);
        var changes = Changes(seed.State, 5001);
        await Append(changes[..5000]);
        await using var server = await Server.Start(database.DataSource);
        using (var exact = await server.Get(Events(seed.Id, 0), server.Jwt(seed.Group)))
        {
            Assert.Equal(HttpStatusCode.OK, exact.StatusCode);
            var page = await Json(exact);
            Assert.Equal(5000, page.GetProperty("lastSeq").GetInt64());
            Assert.Equal(5000, page.GetProperty("events").GetArrayLength());
            Assert.Equal(Enumerable.Range(1, 5000).Select(value => (long)value),
                page.GetProperty("events").EnumerateArray().Select(item => item.GetProperty("seq").GetInt64()));
        }
        await Append(changes[5000..]);
        using var overflow = await server.Get(Events(seed.Id, 0), server.Jwt(seed.Group));
        await Problem(overflow, HttpStatusCode.Conflict, "resync_required");
        Assert.False((await Json(overflow)).TryGetProperty("events", out _));
        using var shifted = await server.Get(Events(seed.Id, 1), server.Jwt(seed.Group));
        Assert.Equal(HttpStatusCode.OK, shifted.StatusCode);
        Assert.Equal(5000, (await Json(shifted)).GetProperty("events").GetArrayLength());
        using var snapshot = await server.Get(Snapshot(seed.Id), server.Jwt(seed.Group));
        Assert.Equal(5001, (await Json(snapshot)).GetProperty("lastSeq").GetInt64());
    }

    [Theory]
    [InlineData("")]
    [InlineData("?afterSeq=-1")]
    [InlineData("?afterSeq=01")]
    [InlineData("?afterSeq=%2B1")]
    [InlineData("?afterSeq=0&afterSeq=0")]
    [InlineData("?afterSeq=0&AfterSeq=0")]
    [InlineData("?AfterSeq=0")]
    [InlineData("?afterSeq=0&limit=1")]
    [InlineData("?afterSeq=0&cursor=x")]
    [InlineData("?afterSeq=9223372036854775808")]
    [InlineData("?afterSeq=1.0")]
    [InlineData("?afterSeq=%30")]
    [InlineData("?%61fterSeq=0")]
    [InlineData("?afterSeq=0&")]
    public async Task Rejects_strict_query_boundary_violations(string query)
    {
        var seed = await Seed();
        await using var server = await Server.Start(database.DataSource);
        using var response = await server.Get(Snapshot(seed.Id) + "/events" + query, server.Jwt(seed.Group));
        await Problem(response, HttpStatusCode.BadRequest, "invalid_run_query");
    }

    [Fact]
    public async Task Empty_runs_future_sequences_and_Int64_limits_have_explicit_unambiguous_responses()
    {
        var seed = await Seed();
        await using var server = await Server.Start(database.DataSource);
        using var empty = await server.Get(Events(seed.Id, 0), server.Jwt(seed.Group));
        Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
        var page = await Json(empty);
        Assert.Equal(0, page.GetProperty("lastSeq").GetInt64());
        Assert.Empty(page.GetProperty("events").EnumerateArray());
        foreach (var after in new[] { 1L, 9007199254740991L, 9007199254740992L, long.MaxValue })
        {
            using var future = await server.Get(Events(seed.Id, after), server.Jwt(seed.Group));
            await Problem(future, HttpStatusCode.BadRequest, "after_seq_ahead");
        }
        await Execute("UPDATE runs SET last_seq=$2,snapshot_seq=$2 WHERE id=$1", seed.Id, long.MaxValue);
        using var maximum = await server.Get(Events(seed.Id, long.MaxValue), server.Jwt(seed.Group));
        Assert.Equal(HttpStatusCode.OK, maximum.StatusCode);
        Assert.Equal(long.MaxValue, (await Json(maximum)).GetProperty("lastSeq").GetInt64());
    }

    [Fact]
    public async Task Fresh_stored_roles_precede_existence_and_tail_queries_and_prevent_resource_enumeration()
    {
        var seed = await Seed();
        var foreign = await Seed();
        await using var server = await Server.Start(database.DataSource);
        var jwt = server.Jwt(seed.Group);
        foreach (var suffix in new[] { "", "/events?afterSeq=0" })
        {
            using var permitted = await server.Get(Snapshot(seed.Id) + suffix, jwt);
            Assert.Equal(HttpStatusCode.OK, permitted.StatusCode);
            using var denied = await server.Get(Snapshot(foreign.Id) + suffix, jwt);
            await Problem(denied, HttpStatusCode.Forbidden, "insufficient_scope_role");
            using var unknown = await server.Get(Snapshot(Guid.CreateVersion7()) + suffix, server.Jwt(role: "Admin"));
            await Problem(unknown, HttpStatusCode.Forbidden, "insufficient_scope_role");
            Assert.Equal((await Json(denied)).GetRawText(), (await Json(unknown)).GetRawText());
            using var anonymous = await server.Get(Snapshot(seed.Id) + suffix, null);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }
        foreach (var invalid in new[]
        {
            server.Jwt(seed.Group, identity: "app"), server.Jwt(seed.Group, role: "Registrar"),
            server.Jwt(seed.Group, role: "Agent"),
            server.Jwt(seed.Group, extra: [new("hasgroups", "true")]),
            server.Jwt(seed.Group, extra: [new("groups", "not-a-uuid")])
        })
        {
            using var denied = await server.Get(Events(seed.Id, 0), invalid);
            await Problem(denied, HttpStatusCode.Forbidden, "insufficient_scope_role");
        }
        using var invalidToken = await server.Get(Events(seed.Id, 0), server.Jwt(seed.Group, identity: null, scope: null));
        Assert.Equal(HttpStatusCode.Unauthorized, invalidToken.StatusCode);
        await Execute("DELETE FROM role_bindings WHERE principal_id=$1", seed.Group);
        await Execute("UPDATE run_snapshots SET payload=$2 WHERE run_id=$1", seed.Id, "secret-sentinel"u8.ToArray());
        using var revoked = await server.Get(Snapshot(seed.Id), jwt);
        await Problem(revoked, HttpStatusCode.Forbidden, "insufficient_scope_role");
        using var revokedBadQuery = await server.Get(Snapshot(seed.Id) + "/events?afterSeq=bad", jwt);
        await Problem(revokedBadQuery, HttpStatusCode.Forbidden, "insufficient_scope_role");
        using var global = await server.Get(Events(seed.Id, 0), server.Jwt(identity: "app", scope: null, role: "Admin"));
        Assert.Equal(HttpStatusCode.OK, global.StatusCode);
    }

    [Theory]
    [InlineData(RegistryRole.Viewer)]
    [InlineData(RegistryRole.Operator)]
    [InlineData(RegistryRole.Author)]
    [InlineData(RegistryRole.Admin)]
    public async Task All_stored_user_roles_can_view_without_token_roles(RegistryRole role)
    {
        var seed = await Seed(role: role);
        await using var server = await Server.Start(database.DataSource);
        using var response = await server.Get(Events(seed.Id, 0), server.Jwt(seed.Group));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var tokenRoleOnly = await server.Get(Events(seed.Id, 0), server.Jwt(role: role.ToString()));
        Assert.Equal(role == RegistryRole.Admin ? HttpStatusCode.OK : HttpStatusCode.Forbidden, tokenRoleOnly.StatusCode);
    }

    [Fact]
    public async Task Uncommitted_and_rolled_back_appends_are_invisible_to_both_routes()
    {
        var seed = await Seed();
        await using var server = await Server.Start(database.DataSource);
        await using var connection = await database.DataSource.OpenConnectionAsync(Token);
        await using var transaction = await connection.BeginTransactionAsync(Token);
        var change = seed.State.CreateEvent(EventEntity.Run, null, (int)RunStatus.Running, DateTimeOffset.UtcNow);
        await WriteEvent(connection, change);
        foreach (var path in new[] { Snapshot(seed.Id), Events(seed.Id, 0) })
        {
            using var response = await server.Get(path, server.Jwt(seed.Group));
            Assert.Equal(0, (await Json(response)).GetProperty("lastSeq").GetInt64());
        }
        await transaction.RollbackAsync(Token);
        using var after = await server.Get(Events(seed.Id, 0), server.Jwt(seed.Group));
        Assert.Empty((await Json(after)).GetProperty("events").EnumerateArray());
        await Append([change]);
        using var committed = await server.Get(Events(seed.Id, 0), server.Jwt(seed.Group));
        Assert.Equal(1, (await Json(committed)).GetProperty("lastSeq").GetInt64());
    }

    [Fact]
    public async Task Concurrent_appends_keep_envelope_sequence_events_and_snapshot_graph_consistent()
    {
        var seed = await Seed(80);
        var changes = Changes(seed.State, 200);
        await using var server = await Server.Start(database.DataSource);
        await using var appender = Appender();
        var writer = Task.Run(async () =>
        {
            foreach (var batch in changes.Chunk(4)) await appender.AppendAsync(batch, Token);
        }, Token);
        for (var index = 0; index < 40; index++)
        {
            using var response = await server.Get(Events(seed.Id, 0), server.Jwt(seed.Group));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var page = await Json(response);
            var sequence = page.GetProperty("lastSeq").GetInt64();
            Assert.Equal(sequence, page.GetProperty("events").GetArrayLength());
            Assert.Equal(Enumerable.Range(1, (int)sequence).Select(value => (long)value),
                page.GetProperty("events").EnumerateArray().Select(item => item.GetProperty("seq").GetInt64()));
            using var snapshot = await server.Get(Snapshot(seed.Id), server.Jwt(seed.Group));
            Assert.Equal(HttpStatusCode.OK, snapshot.StatusCode);
            var state = await Json(snapshot);
            var last = state.GetProperty("lastSeq").GetInt64();
            Assert.All(state.GetProperty("tasks").EnumerateObject(), task =>
            {
                var number = int.Parse(task.Name["node/task".Length..], System.Globalization.CultureInfo.InvariantCulture);
                var transitions = Math.Clamp(last - 1 - number * 3, 0, 3);
                Assert.Equal(transitions, task.Value.GetInt32());
            });
        }
        await writer;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Append_committing_between_header_and_tail_cannot_change_the_read_transaction_snapshot(bool snapshot)
    {
        var seed = await Seed();
        await using var server = await Server.Start(database.DataSource);
        await using var blocker = await database.DataSource.OpenConnectionAsync(Token);
        await using var transaction = await blocker.BeginTransactionAsync(Token);
        await using (var command = new NpgsqlCommand("LOCK TABLE run_events IN ACCESS EXCLUSIVE MODE", blocker))
            await command.ExecuteNonQueryAsync(Token);
        var pending = server.Get(snapshot ? Snapshot(seed.Id) : Events(seed.Id, 0), server.Jwt(seed.Group));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        while (true)
        {
            await using var waiting = database.DataSource.CreateCommand("""
                SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE datname=current_database()
                    AND wait_event_type='Lock' AND query LIKE '%SELECT seq,payload FROM run_events%')
                """);
            if (await waiting.ExecuteScalarAsync(deadline.Token) is true) break;
            await Task.Delay(10, deadline.Token);
        }
        await WriteEvent(blocker, seed.State.CreateEvent(EventEntity.Run, null, (int)RunStatus.Running, DateTimeOffset.UtcNow));
        await transaction.CommitAsync(Token);
        using var before = await pending;
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        var original = await Json(before);
        Assert.Equal(0, original.GetProperty("lastSeq").GetInt64());
        if (snapshot) Assert.Equal((int)RunStatus.Queued, original.GetProperty("status").GetInt32());
        else Assert.Empty(original.GetProperty("events").EnumerateArray());
        using var after = await server.Get(snapshot ? Snapshot(seed.Id) : Events(seed.Id, 0), server.Jwt(seed.Group));
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        Assert.Equal(1, (await Json(after)).GetProperty("lastSeq").GetInt64());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("gap")]
    [InlineData("payload")]
    [InlineData("identity")]
    [InlineData("sequence")]
    [InlineData("state")]
    [InlineData("missing-field")]
    public async Task Missing_gapped_and_corrupt_tails_fail_explicitly_without_payload_disclosure(string corruption)
    {
        var seed = await Seed();
        var changes = Changes(seed.State, 3);
        await Append(changes);
        switch (corruption)
        {
            case "missing": await Execute("DELETE FROM run_events WHERE run_id=$1", seed.Id); break;
            case "gap": await Execute("DELETE FROM run_events WHERE run_id=$1 AND seq=2", seed.Id); break;
            default:
                var payload = corruption switch
                {
                    "payload" => "secret-sentinel"u8.ToArray(),
                    "identity" => JsonSerializer.SerializeToUtf8Bytes(changes[1] with { RunId = Guid.CreateVersion7() }),
                    "sequence" => JsonSerializer.SerializeToUtf8Bytes(changes[1] with { Sequence = 7 }),
                    "missing-field" => JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        changes[1].RunId, changes[1].Sequence, changes[1].Entity, changes[1].Key, changes[1].State
                    }),
                    _ => JsonSerializer.SerializeToUtf8Bytes(changes[1] with { State = int.MaxValue })
                };
                await Execute("UPDATE run_events SET payload=$2 WHERE run_id=$1 AND seq=2", seed.Id, payload);
                break;
        }
        await using var server = await Server.Start(database.DataSource);
        foreach (var path in new[] { Snapshot(seed.Id), Events(seed.Id, 0) })
        {
            using var response = await server.Get(path, server.Jwt(seed.Group));
            await Problem(response, HttpStatusCode.InternalServerError, "run_data_inconsistent");
            Assert.DoesNotContain("secret-sentinel", await response.Content.ReadAsStringAsync(Token));
        }
    }

    [Fact]
    public async Task Terminal_abort_retains_safe_actor_reason_source_and_server_timestamps()
    {
        var seed = await Seed();
        var at = DateTimeOffset.UtcNow;
        var abort = new AbortRecord("actor-id", "Actor name", at, "Stop requested", AbortSource.UI);
        var change = seed.State.Append(EventEntity.Run, null, (int)RunStatus.Aborted, at, abort);
        await Append([change]);
        await using var server = await Server.Start(database.DataSource);
        using var events = await server.Get(Events(seed.Id, 0), server.Jwt(seed.Group));
        Assert.Equal(HttpStatusCode.OK, events.StatusCode);
        var page = await Json(events);
        var wire = page.GetProperty("events")[0];
        Assert.Equal(JsonSerializer.SerializeToElement(LiveEvent.From(change), new JsonSerializerOptions(JsonSerializerDefaults.Web)).GetRawText(),
            wire.GetRawText());
        using var snapshot = await server.Get(Snapshot(seed.Id), server.Jwt(seed.Group));
        Assert.Equal(HttpStatusCode.OK, snapshot.StatusCode);
        var state = await Json(snapshot);
        Assert.Equal((int)RunStatus.Aborted, state.GetProperty("status").GetInt32());
        Assert.Equal(wire.GetProperty("abort").GetRawText(), state.GetProperty("abort").GetRawText());
        Assert.Equal(wire.GetProperty("at").GetString(), state.GetProperty("endedAt").GetString());
    }

    [Fact]
    public async Task Canonical_identity_and_snapshot_query_boundaries_are_enforced()
    {
        var seed = await Seed();
        await using var server = await Server.Start(database.DataSource);
        foreach (var invalid in new[] { seed.Id.ToString("D").ToUpperInvariant(), seed.Id.ToString("N"),
            Guid.NewGuid().ToString("D"), "not-a-run", Guid.Empty.ToString("D"),
            "%30" + seed.Id.ToString("D")[1..] })
        {
            using var response = await server.Get("/api/v1/runs/" + invalid + "/events?afterSeq=0", server.Jwt(seed.Group));
            await Problem(response, HttpStatusCode.BadRequest, "invalid_run_identity");
        }
        using var query = await server.Get(Snapshot(seed.Id) + "?variables=true", server.Jwt(seed.Group));
        await Problem(query, HttpStatusCode.BadRequest, "invalid_run_query");
        await Execute("DELETE FROM run_snapshots WHERE run_id=$1", seed.Id);
        using var missing = await server.Get(Snapshot(seed.Id), server.Jwt(seed.Group));
        await Problem(missing, HttpStatusCode.InternalServerError, "run_data_inconsistent");
    }

    [Theory]
    [InlineData("payload")]
    [InlineData("identity")]
    [InlineData("sequence")]
    [InlineData("header-status")]
    public async Task Corrupt_materialized_snapshots_fail_without_exposing_stored_values(string corruption)
    {
        var seed = await Seed();
        var payload = corruption switch
        {
            "payload" => "secret-sentinel"u8.ToArray(),
            "identity" => JsonSerializer.SerializeToUtf8Bytes(seed.State.Snapshot with { RunId = Guid.CreateVersion7() }),
            "sequence" => JsonSerializer.SerializeToUtf8Bytes(seed.State.Snapshot with { LastSequence = 1 }),
            _ => JsonSerializer.SerializeToUtf8Bytes(seed.State.Snapshot)
        };
        await Execute("UPDATE run_snapshots SET payload=$2 WHERE run_id=$1", seed.Id, payload);
        if (corruption == "header-status") await Execute("UPDATE runs SET status=1 WHERE id=$1", seed.Id);
        await using var server = await Server.Start(database.DataSource);
        using var response = await server.Get(Snapshot(seed.Id), server.Jwt(seed.Group));
        await Problem(response, HttpStatusCode.InternalServerError, "run_data_inconsistent");
        Assert.DoesNotContain("secret-sentinel", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task OpenApi_exposes_actual_resync_snapshot_DTOs_and_the_required_exact_decimal_query()
    {
        await using var server = await Server.Start(database.DataSource);
        using var response = await server.Get("/openapi/v1.json", server.Jwt(role: "Admin"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var document = await Json(response);
        var paths = document.GetProperty("paths");
        var events = paths.GetProperty("/api/v1/runs/{id}/events").GetProperty("get");
        var query = Assert.Single(events.GetProperty("parameters").EnumerateArray(),
            parameter => parameter.GetProperty("name").GetString() == "afterSeq");
        Assert.True(query.GetProperty("required").GetBoolean());
        Assert.Equal("string", query.GetProperty("schema").GetProperty("type").GetString());
        Assert.True(events.GetProperty("responses").TryGetProperty("409", out _));
        Assert.True(paths.TryGetProperty("/api/v1/runs/{id}", out _));
        var schemas = document.GetProperty("components").GetProperty("schemas");
        var page = schemas.GetProperty("RunEventsPage").GetProperty("properties");
        Assert.Equal("int64", page.GetProperty("lastSeq").GetProperty("format").GetString());
        var snapshot = schemas.GetProperty("RunSnapshotResponse").GetProperty("properties");
        Assert.True(snapshot.TryGetProperty("nodes", out _));
        Assert.True(snapshot.TryGetProperty("tasks", out _));
        Assert.True(snapshot.TryGetProperty("abort", out _));
        Assert.False(snapshot.TryGetProperty("variables", out _));
        Assert.False(snapshot.TryGetProperty("outputs", out _));
    }

    private async Task<Seeded> Seed(int tasks = 1, RegistryRole role = RegistryRole.Viewer)
    {
        var team = "resync-" + Guid.NewGuid().ToString("N");
        var group = Guid.NewGuid();
        await using (var connection = await database.DataSource.OpenConnectionAsync(Token))
        await using (var transaction = await connection.BeginTransactionAsync(Token))
        {
            await new RegistryWriter().UpsertAsync(connection, transaction, new(team,
                [new(team, "release", "description", new([]), [new(role, group)])]), Token);
            await transaction.CommitAsync(Token);
        }
        var workflow = team + "/release/workflow";
        var version = Guid.CreateVersion7();
        var state = new RunState(Guid.CreateVersion7(),
            [new("node", [], Enumerable.Range(0, tasks).Select(index => new TaskDefinition("task" + index)).ToImmutableArray())],
            new WorkflowMetadata("release", ImmutableSortedDictionary<string, string>.Empty), Assert.Fail,
            new Dictionary<string, JsonElement> { ["password"] = JsonSerializer.SerializeToElement("secret-sentinel") });
        await Store.CreateAsync(state, workflow, version, DateTimeOffset.UtcNow.AddHours(1), Token);
        return new(state, group, workflow, version);
    }

    private static RunEvent[] Changes(RunState state, int count)
    {
        var events = new List<RunEvent>
        {
            state.Append(EventEntity.Run, null, (int)RunStatus.Running, DateTimeOffset.UtcNow)
        };
        for (var task = 0; events.Count < count; task++)
            foreach (var status in new[] { ExecutionStatus.Ready, ExecutionStatus.Dispatched, ExecutionStatus.Running })
            {
                if (events.Count == count) break;
                events.Add(state.Append(EventEntity.Task, "node/task" + task, (int)status, DateTimeOffset.UtcNow));
            }
        return events.ToArray();
    }

    private async Task Append(RunEvent[] changes)
    {
        await using var appender = Appender();
        foreach (var batch in changes.Chunk(128)) await appender.AppendAsync(batch, Token);
    }

    private GroupCommitAppender Appender() => new(database.DataSource, NullLogger<GroupCommitAppender>.Instance);
    private static string Snapshot(Guid id) => "/api/v1/runs/" + id.ToString("D");
    private static string Events(Guid id, long after) => Snapshot(id) + "/events?afterSeq=" +
        after.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return document.RootElement.Clone();
    }
    private static async Task Problem(HttpResponseMessage response, HttpStatusCode status, string reason)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(reason, (await Json(response)).GetProperty("reason").GetString());
    }

    private async Task Execute(string sql, params object[] values)
    {
        await using var command = database.DataSource.CreateCommand(sql);
        foreach (var value in values) command.Parameters.Add(new() { Value = value });
        await command.ExecuteNonQueryAsync(Token);
    }

    private async Task<object?> Scalar(string sql, params object[] values)
    {
        await using var command = database.DataSource.CreateCommand(sql);
        foreach (var value in values) command.Parameters.Add(new() { Value = value });
        return await command.ExecuteScalarAsync(Token);
    }

    private async Task WriteEvent(NpgsqlConnection connection, RunEvent change)
    {
        await using var command = new NpgsqlCommand("""
            WITH inserted AS (
                INSERT INTO run_events(run_day,run_id,seq,ts,kind,entity_key,payload)
                VALUES(uuid_v7_run_day($1),$1,$2,$3,$4,$5,$6) RETURNING seq
            )
            UPDATE runs SET last_seq=inserted.seq,status=$7 FROM inserted
            WHERE id=$1 AND run_day=uuid_v7_run_day($1)
            """, connection);
        foreach (var value in new object[] { change.RunId, change.Sequence, change.At, (short)change.Entity,
            (object?)change.Key ?? DBNull.Value, JsonSerializer.SerializeToUtf8Bytes(change), (short)change.State })
            command.Parameters.Add(new() { Value = value });
        await command.ExecuteNonQueryAsync(Token);
    }

    private sealed record Seeded(RunState State, Guid Group, string Workflow, Guid Version)
    {
        public Guid Id => State.Snapshot.RunId;
    }

    internal sealed class Server(WebApplication app, HttpClient client, RSA rsa, RsaSecurityKey key) : IAsyncDisposable
    {
        private const string Tenant = "ef8a05a0-451c-4479-9c8d-391b6bc7ee1a";
        private const string Audience = "562b0870-4522-43da-8587-41e9b788f309";
        private const string Issuer = $"https://login.microsoftonline.com/{Tenant}/v2.0";

        public static async Task<Server> Start(NpgsqlDataSource source, bool runs = false, TimeProvider? time = null)
        {
            var rsa = RSA.Create(2048);
            var key = new RsaSecurityKey(rsa) { KeyId = "resync-test" };
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Configuration["AzureAd:TenantId"] = Tenant;
            builder.Configuration["AzureAd:ClientId"] = Audience;
            builder.Services.AddWorkflowAuthentication(builder.Configuration, builder.Environment);
            builder.Services.AddSingleton(source);
            builder.Services.AddTeamAuthorization();
            builder.Services.AddWorkflowOpenApi();
            if (runs)
            {
                builder.Services.AddSingleton(time ?? TimeProvider.System);
                builder.Services.AddSingleton<CommandInbox>();
                builder.Services.AddSingleton<ActiveVersionCache>();
                builder.Services.AddHostedService<ActiveVersionCache>(services => services.GetRequiredService<ActiveVersionCache>());
            }
            builder.Services.ConfigureHttpJsonOptions(options =>
                options.SerializerOptions.TypeInfoResolverChain.Insert(0, RunEventsJsonContext.Default));
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
            app.MapRunEventsEndpoints();
            if (runs) app.MapRunsEndpoints();
            app.MapWorkflowOpenApi();
            await app.StartAsync(TestContext.Current.CancellationToken);
            if (runs)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(10));
                while (!app.Services.GetRequiredService<ActiveVersionCache>().IsReady) await Task.Delay(10, deadline.Token);
            }
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return new(app, new HttpClient { BaseAddress = new(address), Timeout = TimeSpan.FromSeconds(20) }, rsa, key);
        }

        public string Jwt(Guid? group = null, string? identity = "user", string? scope = "api.read",
            string role = "", Claim[]? extra = null)
        {
            var claims = new List<Claim> { new("tid", Tenant), new("ver", "2.0"),
                new("oid", Guid.NewGuid().ToString()), new("azp", "60d24b19-f65a-4ed4-96cc-9174bfcacabc") };
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
            var uri = new Uri(client.BaseAddress!.AbsoluteUri.TrimEnd('/') + path,
                new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true });
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (jwt is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
            return await client.SendAsync(request, TestContext.Current.CancellationToken);
        }

        public async Task<HttpResponseMessage> Trigger(string workflowId, string jwt, string? idempotencyKey = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/runs")
            {
                Content = JsonContent.Create(new TriggerRunRequest(workflowId), RunJsonContext.Default.TriggerRunRequest)
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
            if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
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
