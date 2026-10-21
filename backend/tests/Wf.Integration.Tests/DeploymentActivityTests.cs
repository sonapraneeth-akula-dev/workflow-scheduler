using System.Net;
using System.Text;
using System.Text.Json;
using Wf.ControlPlane.Api.Admin;
using Wf.Integration.Tests.Execution;
using Wf.Integration.Tests.Registration;

namespace Wf.Integration.Tests;

public sealed class DeploymentActivityTests(RegistrationHostDatabase database) : IClassFixture<RegistrationHostDatabase>
{
    private const string Path = "/api/v1/admin/deployment-activity";
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private static DeploymentOperationRequest StartRequest(string? id = null) => new(id ?? Guid.NewGuid().ToString("D"), "Stage", "ha-lab-test");
    private static DeploymentEventRequest Event(int sequence = 1, string phase = "Build", string outcome = "Running") =>
        new(sequence, phase, null, outcome, "Reported build step");

    [Fact]
    public async Task Operations_are_shared_durable_ordered_audited_and_exact_retries_are_idempotent()
    {
        await using var api = await MetricsApiServer.Start(database.DataSource, Token);
        var jwt = api.Jwt(role: "Admin");
        var request = StartRequest();
        var initial = await Start(api, jwt, request);
        Assert.Equal("Running", initial.Status);
        Assert.Equal(0, initial.Sequence);
        Assert.Empty(initial.Events);
        var repeated = await Start(api, jwt, request);
        Assert.Equal(JsonSerializer.Serialize(initial, DeploymentActivityJson.Default.DeploymentOperation),
            JsonSerializer.Serialize(repeated, DeploymentActivityJson.Default.DeploymentOperation));
        using (var first = await Append(api, jwt, initial.Id, Event()))
        {
            var operation = await Read(first);
            Assert.Equal(1, operation.Sequence);
            Assert.Equal("Build", Assert.Single(operation.Events).Phase);
        }
        using (var duplicate = await Append(api, jwt, initial.Id, Event())) Assert.Equal(1, (await Read(duplicate)).Sequence);
        using (var completed = await Append(api, jwt, initial.Id, Event(2, "Complete", "Succeeded")))
            Assert.Equal("Succeeded", (await Read(completed)).Status);
        using (var retry = await Append(api, jwt, initial.Id, Event(2, "Complete", "Succeeded")))
            Assert.Equal("Succeeded", (await Read(retry)).Status);
        await using var replica = await MetricsApiServer.Start(database.DataSource, Token);
        using var history = await replica.Get(Path, replica.Jwt(role: "Admin"), Token);
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        Assert.True(history.Headers.CacheControl!.NoStore);
        var value = JsonSerializer.Deserialize(await history.Content.ReadAsStringAsync(Token),
            DeploymentActivityJson.Default.DeploymentActivityHistory)!;
        var durable = Assert.Single(value.Operations, operation => operation.Id == initial.Id);
        Assert.Equal(new[] { 1, 2 }, durable.Events.Select(item => item.Sequence));
        Assert.Equal("Succeeded", durable.Status);
        Assert.Equal(3L, await AuditCount(initial.Id));
    }

    [Theory]
    [InlineData(null, 401)]
    [InlineData("", 403)]
    [InlineData("Viewer", 403)]
    [InlineData("Operator", 403)]
    [InlineData("Registrar", 403)]
    [InlineData("Agent", 403)]
    public async Task All_activity_reads_and_writes_require_global_Admin(string? role, int status)
    {
        await using var api = await MetricsApiServer.Start(database.DataSource, Token);
        var jwt = role is null ? null : api.Jwt(role: role);
        using var get = await api.Get(Path, jwt, Token);
        using var start = await Post(api, jwt, Path, JsonSerializer.Serialize(StartRequest(), DeploymentActivityJson.Default.DeploymentOperationRequest));
        using var report = await Append(api, jwt, Guid.NewGuid().ToString("D"), Event());
        Assert.Equal(status, (int)get.StatusCode);
        Assert.Equal(status, (int)start.StatusCode);
        Assert.Equal(status, (int)report.StatusCode);
    }

    [Fact]
    public async Task Only_initiator_reports_and_sequence_gaps_conflicting_replays_and_postterminal_reports_fail()
    {
        await using var api = await MetricsApiServer.Start(database.DataSource, Token);
        var jwt = api.Jwt(role: "Admin");
        var initial = await Start(api, jwt, StartRequest());
        using (var other = await Append(api, api.Jwt(role: "Admin"), initial.Id, Event()))
            Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
        using (var gap = await Append(api, jwt, initial.Id, Event(2)))
            Assert.Equal(HttpStatusCode.Conflict, gap.StatusCode);
        using (var first = await Append(api, jwt, initial.Id, Event())) await Read(first);
        using (var conflict = await Append(api, jwt, initial.Id, Event() with { Description = "Changed event" }))
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        using (var failed = await Append(api, jwt, initial.Id, Event(2, "Failed", "Failed")))
            Assert.Equal("Failed", (await Read(failed)).Status);
        using (var late = await Append(api, jwt, initial.Id, Event(3)))
            Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        Assert.Equal(3L, await AuditCount(initial.Id));
    }

    [Fact]
    public async Task Concurrent_identical_reports_commit_once_and_changed_reports_conflict()
    {
        await using var api = await MetricsApiServer.Start(database.DataSource, Token);
        var jwt = api.Jwt(role: "Admin");
        var operation = await Start(api, jwt, StartRequest());
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Append(api, jwt, operation.Id, Event())));
        try { Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode)); }
        finally { foreach (var response in responses) response.Dispose(); }
        Assert.Equal(2L, await AuditCount(operation.Id));
        var changed = await Task.WhenAll(Enumerable.Range(0, 8).Select(index =>
            Append(api, jwt, operation.Id, Event(2) with { Description = $"Competing event {index}" })));
        try
        {
            Assert.Single(changed, response => response.StatusCode == HttpStatusCode.OK);
            Assert.Equal(7, changed.Count(response => response.StatusCode == HttpStatusCode.Conflict));
        }
        finally { foreach (var response in changed) response.Dispose(); }
        Assert.Equal(3L, await AuditCount(operation.Id));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("""{"operationId":"invalid","mode":"Stage","target":"tag"}""")]
    [InlineData("""{"operationId":"00000000-0000-0000-0000-000000000000","mode":"Stage","target":"tag"}""")]
    [InlineData("""{"operationId":"a0000000-0000-0000-0000-000000000001","mode":"Stop","target":"tag"}""")]
    [InlineData("""{"operationId":"a0000000-0000-0000-0000-000000000001","mode":"Stage","target":"tag\n"}""")]
    [InlineData("""{"operationId":"a0000000-0000-0000-0000-000000000001","mode":"Stage","target":"tag","extra":1}""")]
    [InlineData("""{"operationId":"a0000000-0000-0000-0000-000000000001","mode":"Stage","mode":"Release","target":"tag"}""")]
    public async Task Invalid_start_body_is_400(string json)
    {
        await using var api = await MetricsApiServer.Start(database.DataSource, Token);
        using var response = await Post(api, api.Jwt(role: "Admin"), Path, json);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(0, "Build", "Running")]
    [InlineData(129, "Build", "Running")]
    [InlineData(1, "Invented", "Running")]
    [InlineData(1, "Build", "Failed")]
    [InlineData(1, "Complete", "Running")]
    [InlineData(1, "Failed", "Succeeded")]
    public async Task Invalid_event_boundaries_and_terminal_semantics_are_400(int sequence, string phase, string outcome)
    {
        await using var api = await MetricsApiServer.Start(database.DataSource, Token);
        using var response = await Append(api, api.Jwt(role: "Admin"), Guid.NewGuid().ToString("D"), Event(sequence, phase, outcome));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Body_content_type_size_identity_and_query_errors_are_explicit()
    {
        await using var api = await MetricsApiServer.Start(database.DataSource, Token);
        var jwt = api.Jwt(role: "Admin");
        using (var text = await Post(api, jwt, Path, "{}", "text/plain"))
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, text.StatusCode);
        using (var huge = await Post(api, jwt, Path, new string(' ', 8193)))
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, huge.StatusCode);
        using (var query = await api.Get(Path + "?unknown=1", jwt, Token))
            Assert.Equal(HttpStatusCode.BadRequest, query.StatusCode);
        using var identity = await Post(api, api.Jwt(role: "Admin", actor: Guid.Empty.ToString("D")), Path,
            JsonSerializer.Serialize(StartRequest(), DeploymentActivityJson.Default.DeploymentOperationRequest));
        Assert.Equal(HttpStatusCode.Forbidden, identity.StatusCode);
    }

    [Fact]
    public async Task Audit_failure_rolls_back_operation_event_and_head_instead_of_reporting_success()
    {
        await using var api = await MetricsApiServer.Start(database.DataSource, Token);
        var jwt = api.Jwt(role: "Admin");
        var request = StartRequest();
        var name = "reject_deployment_" + Guid.NewGuid().ToString("N");
        await Execute($"""
            CREATE FUNCTION {name}() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'injected deployment audit failure'; END $$;
            CREATE TRIGGER {name} BEFORE INSERT ON audit_log FOR EACH ROW
                WHEN (NEW.target_kind='Deployment') EXECUTE FUNCTION {name}();
            """);
        try
        {
            using var failed = await Post(api, jwt, Path, JsonSerializer.Serialize(request, DeploymentActivityJson.Default.DeploymentOperationRequest));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
            Assert.DoesNotContain("injected", await failed.Content.ReadAsStringAsync(Token));
            Assert.Equal(0L, await AuditCount(request.OperationId));
        }
        finally { await Execute($"DROP TRIGGER {name} ON audit_log; DROP FUNCTION {name}();"); }
        var operation = await Start(api, jwt, request);
        using var retried = await Append(api, jwt, operation.Id, Event());
        Assert.Equal(1, (await Read(retried)).Sequence);
    }

    [Fact]
    public async Task Append_audit_failure_rolls_back_event_and_head_and_allows_exact_retry()
    {
        await using var api = await MetricsApiServer.Start(database.DataSource, Token);
        var jwt = api.Jwt(role: "Admin");
        var operation = await Start(api, jwt, StartRequest());
        var name = "reject_deployment_" + Guid.NewGuid().ToString("N");
        await Execute($"""
            CREATE FUNCTION {name}() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'injected deployment audit failure'; END $$;
            CREATE TRIGGER {name} BEFORE INSERT ON audit_log FOR EACH ROW
                WHEN (NEW.target_kind='Deployment') EXECUTE FUNCTION {name}();
            """);
        try
        {
            using var failed = await Append(api, jwt, operation.Id, Event(1, "Complete", "Succeeded"));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
            Assert.DoesNotContain("injected", await failed.Content.ReadAsStringAsync(Token));
            Assert.Equal(1L, await AuditCount(operation.Id));
            using var history = await api.Get(Path, jwt, Token);
            var value = JsonSerializer.Deserialize(await history.Content.ReadAsStringAsync(Token),
                DeploymentActivityJson.Default.DeploymentActivityHistory)!;
            var unchanged = Assert.Single(value.Operations, item => item.Id == operation.Id);
            Assert.Equal("Running", unchanged.Status);
            Assert.Equal(0, unchanged.Sequence);
            Assert.Empty(unchanged.Events);
            Assert.Equal(operation.LastReportedAt, unchanged.LastReportedAt);
        }
        finally { await Execute($"DROP TRIGGER {name} ON audit_log; DROP FUNCTION {name}();"); }
        using var retried = await Append(api, jwt, operation.Id, Event(1, "Complete", "Succeeded"));
        var completed = await Read(retried);
        Assert.Equal("Succeeded", completed.Status);
        Assert.Single(completed.Events);
        Assert.Equal(2L, await AuditCount(operation.Id));
    }

    [Fact]
    public async Task Same_object_id_with_different_actor_kind_cannot_retry_start_or_report()
    {
        await using var api = await MetricsApiServer.Start(database.DataSource, Token);
        var actor = Guid.NewGuid().ToString("D");
        var jwt = api.Jwt(role: "Admin", identity: "user", actor: actor);
        var other = api.Jwt(role: "Admin", identity: "app", actor: actor);
        var request = StartRequest();
        var operation = await Start(api, jwt, request);
        using var start = await Post(api, other, Path, JsonSerializer.Serialize(request, DeploymentActivityJson.Default.DeploymentOperationRequest));
        Assert.Equal(HttpStatusCode.Conflict, start.StatusCode);
        using var report = await Append(api, other, operation.Id, Event());
        Assert.Equal(HttpStatusCode.Forbidden, report.StatusCode);
        Assert.Equal(1L, await AuditCount(operation.Id));
    }

    private async Task<DeploymentOperation> Start(MetricsApiServer api, string jwt, DeploymentOperationRequest request)
    {
        using var response = await Post(api, jwt, Path, JsonSerializer.Serialize(request, DeploymentActivityJson.Default.DeploymentOperationRequest));
        return await Read(response);
    }
    private Task<HttpResponseMessage> Append(MetricsApiServer api, string? jwt, string id, DeploymentEventRequest request) =>
        Post(api, jwt, $"{Path}/{id}/events", JsonSerializer.Serialize(request, DeploymentActivityJson.Default.DeploymentEventRequest));
    private Task<HttpResponseMessage> Post(MetricsApiServer api, string? jwt, string path, string json, string type = "application/json") =>
        api.Send(HttpMethod.Post, path, jwt, new StringContent(json, Encoding.UTF8, type), Token);
    private async Task<DeploymentOperation> Read(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonSerializer.Deserialize(text, DeploymentActivityJson.Default.DeploymentOperation)!;
    }
    private async Task<long> AuditCount(string id)
    {
        await using var command = database.DataSource.CreateCommand("SELECT count(*) FROM audit_log WHERE target_kind='Deployment' AND target_id=$1");
        command.Parameters.AddWithValue(id);
        return (long)(await command.ExecuteScalarAsync(Token))!;
    }
    private async Task Execute(string sql)
    {
        await using var command = database.DataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(Token);
    }
}
