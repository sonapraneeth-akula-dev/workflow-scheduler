using System.Net;
using System.Text.Json;
using Npgsql;
using Wf.Contracts.Runs;
using static Wf.Integration.Tests.Registration.RegistrationTests;

namespace Wf.Integration.Tests.Registration;

public sealed partial class TriggerRunTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuditCoverage_trigger_commits_safe_actor_target_before_after_once_per_new_command(bool idempotent)
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var request = await Request(server);
        var group = Guid.NewGuid();
        var version = await Register(server, request, group);
        var body = new TriggerRunRequest(request.WorkflowId!);
        var bearer = Operator(server, group);
        var key = Guid.NewGuid().ToString("D");
        var first = idempotent ? await AcceptedKey(server, body, bearer, key) : await Accepted(server, body, bearer);
        var second = idempotent ? await AcceptedKey(server, body, bearer, key) : await Accepted(server, body, bearer);
        await AssertTriggerAudit(first, request.WorkflowId!, version, null, server.Clock.Now);
        if (idempotent) Assert.Equal(first, second);
        else
        {
            Assert.NotEqual(first, second);
            await AssertTriggerAudit(second, request.WorkflowId!, version, null, server.Clock.Now);
        }
        Assert.Empty(server.Failures);
    }

    [Fact]
    public async Task AuditCoverage_run_now_records_series_without_schedule_variables_or_creating_a_run()
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var (request, group, version, schedule, series) = await RunNowSchedule(server);
        var run = await RunNowAccepted(server, schedule, Operator(server, group));
        await AssertTriggerAudit(run, request.WorkflowId!, version, series, server.Clock.Now);
        Assert.Equal(0L, await Scalar("SELECT count(*) FROM runs WHERE id=$1", run));
        Assert.Empty(server.Failures);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuditCoverage_failed_audit_rolls_back_command_idempotency_and_notification(bool idempotent)
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var request = await Request(server);
        var group = Guid.NewGuid();
        await Register(server, request, group);
        var constraint = "test_audit_" + Guid.NewGuid().ToString("N");
        var key = Guid.NewGuid().ToString("D");
        var workflow = request.WorkflowId!;
        await using var listener = await database.DataSource.OpenConnectionAsync(Token);
        await using (var listen = new NpgsqlCommand("LISTEN commands", listener))
            await listen.ExecuteNonQueryAsync(Token);
        await Execute($"ALTER TABLE audit_log ADD CONSTRAINT {constraint} CHECK(action<>'run.trigger') NOT VALID");
        try
        {
            var body = new TriggerRunRequest(request.WorkflowId!);
            var bearer = Operator(server, group);
            using var response = idempotent ? await Keyed(server, body, bearer, [key]) : await Trigger(server, body, bearer);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.DoesNotContain(constraint, await response.Content.ReadAsStringAsync(Token));
            Assert.Equal(0L, await Scalar("SELECT count(*) FROM commands WHERE payload->>'WorkflowId'=$1", request.WorkflowId!));
            Assert.Equal(0L, await Scalar("SELECT count(*) FROM idempotency_keys WHERE key=$1", key));
            Assert.Equal(0L, await Scalar("SELECT count(*) FROM audit_log WHERE action='run.trigger' AND team_id=$1",
                workflow[..workflow.IndexOf('/')]));
            Assert.False(await listener.WaitAsync(100, Token));
        }
        finally { await Execute($"ALTER TABLE audit_log DROP CONSTRAINT {constraint}"); }
        Assert.Empty(server.Failures);
    }

    private async Task AssertTriggerAudit(Guid run, string workflow, Guid version, Guid? series, DateTimeOffset at)
    {
        await using var query = database.DataSource.CreateCommand("""
            SELECT actor,actor_kind,action,target_kind,team_id,ts,summary::text
            FROM audit_log WHERE target_id=$1
            """);
        query.Parameters.AddWithValue(run.ToString("D"));
        await using var row = await query.ExecuteReaderAsync(Token);
        Assert.True(await row.ReadAsync(Token));
        Assert.Equal(Server.Actor.ToString("D"), row.GetString(0));
        Assert.Equal("User", row.GetString(1));
        Assert.Equal(series is null ? "run.trigger" : "schedule.run-now", row.GetString(2));
        Assert.Equal("Run", row.GetString(3));
        Assert.Equal(workflow[..workflow.IndexOf('/')], row.GetString(4));
        Assert.Equal(at.AddTicks(-(at.Ticks % 10)), row.GetFieldValue<DateTimeOffset>(5));
        var json = row.GetString(6);
        using var summary = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Null, summary.RootElement.GetProperty("before").ValueKind);
        var after = summary.RootElement.GetProperty("after");
        Assert.Equal("Queued", after.GetProperty("status").GetString());
        Assert.Equal(workflow, after.GetProperty("workflowId").GetString());
        Assert.Equal(version, after.GetProperty("versionId").GetGuid());
        if (series is { } id) Assert.Equal(id, after.GetProperty("seriesId").GetGuid());
        else Assert.Equal(JsonValueKind.Null, after.GetProperty("seriesId").ValueKind);
        foreach (var forbidden in new[] { "variables", "env", "category", "count", "secret", "uploadUri" })
            Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase);
        Assert.False(await row.ReadAsync(Token));
    }
}
