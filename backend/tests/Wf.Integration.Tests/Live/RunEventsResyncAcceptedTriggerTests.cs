using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Contracts.Events;
using Wf.Contracts.Manifest;
using Wf.Contracts.Registry;
using Wf.ControlPlane.Data;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Events;
using Wf.ControlPlane.Engine.Leadership;
using Wf.ControlPlane.Engine.Recovery;
using Wf.ControlPlane.Registry;
using Wf.Integration.Tests.Agents;

namespace Wf.Integration.Tests.Live;

public sealed class RunEventsResyncAcceptedTriggerTests(RunEventsResyncAcceptedTriggerDatabase database) :
    IClassFixture<RunEventsResyncAcceptedTriggerDatabase>
{
    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Accepted_trigger_is_readable_before_recovery_across_local_and_UTC_days_and_after_materialization()
    {
        await using var source = PacificSource();
        var clock = new PacificClock();
        var seed = await Register(source);
        await using var server = await RunEventsResyncTests.Server.Start(source, runs: true, time: clock);
        var jwt = server.Jwt(seed.Group);
        var key = Guid.NewGuid().ToString("D");
        var run = await Trigger(server, seed.Workflow, jwt, key);
        Assert.Equal(run, await Trigger(server, seed.Workflow, jwt, key));
        Assert.Equal(0L, await Scalar(source, "SELECT count(*) FROM runs WHERE id=$1", run));
        Assert.Equal(1L, await Scalar(source,
            "SELECT count(*) FROM commands WHERE run_id=$1 AND processed_at IS NULL", run));
        Assert.Equal("2026-10-16", await Scalar(source, "SELECT uuid_v7_run_day($1)::text", run));
        Assert.Equal("2026-10-15", await Scalar(source,
            "SELECT created_at::date::text FROM commands WHERE run_id=$1", run));
        Assert.Equal(TimeSpan.FromHours(-7), clock.GetLocalNow().Offset);
        JsonElement queued = default;
        for (var index = 0; index < 10; index++)
        {
            using var snapshot = await server.Get(Snapshot(run), jwt);
            Assert.Equal(HttpStatusCode.OK, snapshot.StatusCode);
            Assert.True(snapshot.Headers.CacheControl!.NoStore);
            queued = await Json(snapshot);
            Assert.Equal(run, queued.GetProperty("runId").GetGuid());
            Assert.Equal(seed.Workflow, queued.GetProperty("workflowId").GetString());
            Assert.Equal(seed.Version, queued.GetProperty("versionId").GetGuid());
            Assert.Equal((int)RunStatus.Queued, queued.GetProperty("status").GetInt32());
            Assert.Equal(0, queued.GetProperty("lastSeq").GetInt64());
            Assert.Equal(0, queued.GetProperty("nodes").GetProperty("node").GetInt32());
            Assert.Equal(0, queued.GetProperty("tasks").GetProperty("node/task").GetInt32());
            Assert.Equal(JsonValueKind.Null, queued.GetProperty("abort").ValueKind);
            Assert.False(queued.TryGetProperty("variables", out _));
            Assert.DoesNotContain("secret-sentinel", queued.GetRawText());
            using var events = await server.Get(Events(run), jwt);
            Assert.Equal(HttpStatusCode.OK, events.StatusCode);
            Assert.True(events.Headers.CacheControl!.NoStore);
            var page = await Json(events);
            Assert.Equal(0, page.GetProperty("lastSeq").GetInt64());
            Assert.Empty(page.GetProperty("events").EnumerateArray());
        }
        using var future = await server.Get(Snapshot(run) + "/events?afterSeq=1", jwt);
        await Problem(future, HttpStatusCode.BadRequest, "after_seq_ahead");
        Assert.Equal(0L, await Scalar(source, "SELECT count(*) FROM runs WHERE id=$1", run));
        using var leader = Leader(source);
        Assert.True(await leader.TryAcquireOrRenewAsync(Token));
        try
        {
            var store = new RunStore(source, NullLogger<RunStore>.Instance, leader);
            using var recovery = new RunRecovery(source, store, leader, clock, NullLogger<RunRecovery>.Instance);
            await recovery.RecoverAsync(Token);
            Assert.Equal(1L, await Scalar(source, "SELECT count(*) FROM runs WHERE id=$1", run));
            Assert.Equal(1L, await Scalar(source,
                "SELECT count(*) FROM commands WHERE run_id=$1 AND processed_at IS NOT NULL", run));
            using var snapshot = await server.Get(Snapshot(run), jwt);
            Assert.Equal(HttpStatusCode.OK, snapshot.StatusCode);
            Assert.True(JsonElement.DeepEquals(queued, await Json(snapshot)));
            var state = recovery.RecoveredRuns[run].State;
            var change = state.CreateEvent(EventEntity.Run, null, (int)RunStatus.Running, clock.GetUtcNow());
            await using var appender = new GroupCommitAppender(source, NullLogger<GroupCommitAppender>.Instance);
            await appender.AppendAsync(change, Token);
            using var events = await server.Get(Events(run), jwt);
            Assert.Equal(HttpStatusCode.OK, events.StatusCode);
            var page = await Json(events);
            Assert.Equal(1, page.GetProperty("lastSeq").GetInt64());
            Assert.Equal(1, page.GetProperty("events").GetArrayLength());
            using var running = await server.Get(Snapshot(run), jwt);
            var runningState = await Json(running);
            Assert.Equal((int)RunStatus.Running, runningState.GetProperty("status").GetInt32());
            Assert.Equal(1, runningState.GetProperty("lastSeq").GetInt64());
        }
        finally { await leader.StopAsync(Token); }
    }

    [Fact]
    public async Task Accepted_pending_trigger_uses_fresh_scoped_roles_and_never_enumerates_other_commands()
    {
        var source = database.DataSource;
        var seed = await Register(source);
        await using var server = await RunEventsResyncTests.Server.Start(source, runs: true);
        var jwt = server.Jwt(seed.Group);
        var run = await Trigger(server, seed.Workflow, jwt);
        foreach (var path in new[] { Snapshot(run), Events(run) })
        {
            using var allowed = await server.Get(path, jwt);
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
            using var foreign = await server.Get(path, server.Jwt(Guid.NewGuid()));
            await Problem(foreign, HttpStatusCode.Forbidden, "insufficient_scope_role");
            using var anonymous = await server.Get(path, null);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            using var app = await server.Get(path, server.Jwt(seed.Group, identity: "app", scope: null, role: "Operator"));
            await Problem(app, HttpStatusCode.Forbidden, "insufficient_scope_role");
            using var global = await server.Get(path, server.Jwt(identity: "app", scope: null, role: "Admin"));
            Assert.Equal(HttpStatusCode.OK, global.StatusCode);
        }
        await Execute(source, "DELETE FROM role_bindings WHERE principal_id=$1", seed.Group);
        using var revoked = await server.Get(Snapshot(run), jwt);
        await Problem(revoked, HttpStatusCode.Forbidden, "insufficient_scope_role");
        using var unknown = await server.Get(Snapshot(Guid.CreateVersion7()), jwt);
        await Problem(unknown, HttpStatusCode.Forbidden, "insufficient_scope_role");
        Assert.True(JsonElement.DeepEquals(await Json(revoked), await Json(unknown)));
        using var invalidQuery = await server.Get(Snapshot(run) + "/events?afterSeq=bad", jwt);
        await Problem(invalidQuery, HttpStatusCode.Forbidden, "insufficient_scope_role");
    }

    [Fact]
    public async Task Recovery_committing_during_pending_reads_never_loses_the_durable_owner_or_state()
    {
        var source = database.DataSource;
        var seed = await Register(source);
        await using var server = await RunEventsResyncTests.Server.Start(source, runs: true);
        var jwt = server.Jwt(seed.Group);
        var run = await Trigger(server, seed.Workflow, jwt);
        using var leader = Leader(source);
        Assert.True(await leader.TryAcquireOrRenewAsync(Token));
        try
        {
            var store = new RunStore(source, NullLogger<RunStore>.Instance, leader);
            using var recovery = new RunRecovery(source, store, leader, TimeProvider.System, NullLogger<RunRecovery>.Instance);
            var reads = Enumerable.Range(0, 40).Select(async index =>
            {
                using var response = await server.Get(index % 2 == 0 ? Snapshot(run) : Events(run), jwt);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal(0, (await Json(response)).GetProperty("lastSeq").GetInt64());
            }).ToArray();
            await recovery.RecoverAsync(Token);
            await Task.WhenAll(reads);
            Assert.Equal(1L, await Scalar(source, "SELECT count(*) FROM runs WHERE id=$1", run));
            using var after = await server.Get(Snapshot(run), jwt);
            Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        }
        finally { await leader.StopAsync(Token); }
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("sequence")]
    [InlineData("deadline")]
    [InlineData("missing-field")]
    [InlineData("processed")]
    public async Task Invalid_or_processed_pending_commands_are_not_silently_projected(string corruption)
    {
        var source = database.DataSource;
        var seed = await Register(source);
        await using var server = await RunEventsResyncTests.Server.Start(source, runs: true);
        var jwt = server.Jwt(seed.Group);
        var run = await Trigger(server, seed.Workflow, jwt);
        try
        {
            switch (corruption)
            {
                case "identity":
                    await Execute(source, """
                        UPDATE commands SET payload=jsonb_set(payload,'{InitialState,RunId}',to_jsonb($2::text))
                        WHERE run_id=$1
                        """, run, Guid.CreateVersion7().ToString("D"));
                    break;
                case "sequence":
                    await Execute(source, """
                        UPDATE commands SET payload=jsonb_set(payload,'{InitialState,LastSequence}','1')
                        WHERE run_id=$1
                        """, run);
                    break;
                case "deadline":
                    await Execute(source, """
                        UPDATE commands SET payload=jsonb_set(payload,'{DeadlineAt}','"2000-01-01T00:00:00+00:00"')
                        WHERE run_id=$1
                        """, run);
                    break;
                case "missing-field":
                    await Execute(source, """
                        UPDATE commands SET payload=jsonb_set(payload,'{InitialState}',(payload->'InitialState')-'LastSequence')
                        WHERE run_id=$1
                        """, run);
                    break;
                default:
                    await Execute(source, "UPDATE commands SET processed_at=created_at WHERE run_id=$1", run);
                    break;
            }
            foreach (var path in new[] { Snapshot(run), Events(run) })
            {
                using var response = await server.Get(path, jwt);
                await Problem(response, corruption == "processed" ? HttpStatusCode.Forbidden : HttpStatusCode.InternalServerError,
                    corruption == "processed" ? "insufficient_scope_role" : "run_data_inconsistent");
                Assert.DoesNotContain("secret-sentinel", await response.Content.ReadAsStringAsync(Token));
            }
        }
        finally { await Execute(source, "DELETE FROM commands WHERE run_id=$1", run); }
    }

    private NpgsqlDataSource PacificSource() => NpgsqlDataSource.Create(
        new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            Options = "-c TimeZone=America/Los_Angeles"
        }.ConnectionString);

    private async Task<Seeded> Register(NpgsqlDataSource source)
    {
        var team = "accepted-" + Guid.NewGuid().ToString("N");
        var group = Guid.NewGuid();
        await using (var connection = await source.OpenConnectionAsync(Token))
        await using (var transaction = await connection.BeginTransactionAsync(Token))
        {
            await new RegistryWriter().UpsertAsync(connection, transaction, new(team,
                [new(team, "release", "description", new([]), [new(RegistryRole.Operator, group)])]), Token);
            await transaction.CommitAsync(Token);
        }
        var workflow = team + "/release/workflow";
        var version = Guid.CreateVersion7();
        var manifest = new WorkflowManifest("1.0.0", workflow, "1.0.0",
            new("release", ImmutableSortedDictionary<string, string>.Empty),
            [new("password", VariableType.String, true, JsonSerializer.SerializeToElement("secret-sentinel"))],
            [], [new("node", [], [new("task")])], TimeSpan.FromHours(1));
        await Execute(source, "INSERT INTO workflows(id,type_id) VALUES($1,$2)", workflow, team + "/release");
        await Execute(source, """
            INSERT INTO workflow_versions(id,workflow_id,semver,manifest,package_uri,package_sha256,package_size,
                source_uri,source_sha256,source_size,git_commit,git_author,registered_by,sdk_version)
            VALUES($1,$2,'1.0.0',$3::jsonb,'packages/'||encode($4::bytea,'hex')||'.zip',$4,1,
                'sources/'||encode($4::bytea,'hex'),$4,1,$5,'test','test','1.0.0')
            """, version, workflow, JsonSerializer.Serialize(manifest, WorkflowManifestJsonContext.Default.WorkflowManifest),
            new byte[32], new string('a', 40));
        await Execute(source, "UPDATE workflows SET active_version_id=$2,latest_version_id=$2 WHERE id=$1", workflow, version);
        return new(workflow, version, group);
    }

    private static LeaseManager Leader(NpgsqlDataSource source) => new(source,
        new(Guid.NewGuid().ToString("N"), new Uri("http://localhost:5200")), NullLogger<LeaseManager>.Instance);

    private static async Task<Guid> Trigger(RunEventsResyncTests.Server server, string workflow, string jwt, string? key = null)
    {
        using var response = await server.Trigger(workflow, jwt, key);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await Json(response)).GetProperty("runId").GetGuid();
    }

    private static string Snapshot(Guid run) => "/api/v1/runs/" + run.ToString("D");
    private static string Events(Guid run) => Snapshot(run) + "/events?afterSeq=0";

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return document.RootElement.Clone();
    }

    private static async Task Problem(HttpResponseMessage response, HttpStatusCode status, string reason)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(reason, (await Json(response)).GetProperty("reason").GetString());
    }

    private async Task Execute(NpgsqlDataSource source, string sql, params object[] values)
    {
        await using var command = source.CreateCommand(sql);
        foreach (var value in values) command.Parameters.Add(new() { Value = value });
        await command.ExecuteNonQueryAsync(Token);
    }

    private async Task<object?> Scalar(NpgsqlDataSource source, string sql, params object[] values)
    {
        await using var command = source.CreateCommand(sql);
        foreach (var value in values) command.Parameters.Add(new() { Value = value });
        return await command.ExecuteScalarAsync(Token);
    }

    private sealed record Seeded(string Workflow, Guid Version, Guid Group);

    private sealed class PacificClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 16, 2, 18, 44, TimeSpan.Zero);
        public override TimeZoneInfo LocalTimeZone { get; } =
            TimeZoneInfo.CreateCustomTimeZone("UTC-7", TimeSpan.FromHours(-7), "UTC-7", "UTC-7");
    }
}

public sealed class RunEventsResyncAcceptedTriggerDatabase : AgentDatabase
{
    public string ConnectionString => DatabaseConnectionString;
    protected override IReadOnlyList<string> Migrations => Migrator.LoadScripts().Select(script => script.Name).ToArray();
}
