using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Wf.Contracts;
using Wf.Contracts.Manifest;
using Wf.Contracts.Registry;
using Wf.Contracts.Runs;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Leadership;
using Wf.ControlPlane.Engine.Recovery;

namespace Wf.Integration.Tests.Registration;

public sealed partial class RegistrationTests
{
    [Fact]
    public async Task Clone_source_includes_pinned_defaults_when_the_stored_run_did_not_supply_an_override()
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var seed = await SeedRerun(server, omitVariables: true);
        Assert.Empty(seed.Snapshot.Variables);
        using var source = await server.Send(HttpMethod.Get, $"/api/v1/runs/{seed.Snapshot.RunId:D}/clone",
            null!, RerunOperator(server, seed.Group));
        Assert.Equal(HttpStatusCode.OK, source.StatusCode);
        var inputs = (await source.Content.ReadFromJsonAsync(RunJsonContext.Default.CloneRunSourceResponse, TestContext.Current.CancellationToken))!;
        Assert.Equal("dev", inputs.Variables["environment"].GetString());
        Assert.Empty(server.Failures);
    }

    [Fact]
    public async Task Clone_copies_pinned_inputs_and_metadata_but_restarts_every_node_with_edited_variables_and_parent_lineage()
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var seed = await SeedRerun(server);
        var bearer = RerunOperator(server, seed.Group);
        using var originalResponse = await server.Send(HttpMethod.Get, $"/api/v1/runs/{seed.Snapshot.RunId:D}", null!, bearer);
        var original = await originalResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var source = await server.Send(HttpMethod.Get, $"/api/v1/runs/{seed.Snapshot.RunId:D}/clone", null!, bearer);
        Assert.Equal(HttpStatusCode.OK, source.StatusCode);
        Assert.True(source.Headers.CacheControl!.NoStore);
        var inputs = (await source.Content.ReadFromJsonAsync(RunJsonContext.Default.CloneRunSourceResponse, TestContext.Current.CancellationToken))!;
        Assert.Equal(seed.Snapshot.RunId, inputs.RunId);
        Assert.Equal(seed.Version, inputs.VersionId);
        Assert.Equal(seed.Request.WorkflowId, inputs.WorkflowId);
        Assert.Equal("ppe", inputs.Variables["environment"].GetString());
        Assert.DoesNotContain("package-v1.0.0", await source.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var manifest = seed.Request.Manifest!.Value.Deserialize(WorkflowManifestJsonContext.Default.WorkflowManifest)!;
        using var newer = await server.Post(seed.Request with
        {
            Manifest = JsonSerializer.SerializeToElement(manifest with { Version = "2.0.0" },
                WorkflowManifestJsonContext.Default.WorkflowManifest)
        });
        Assert.Equal(HttpStatusCode.Created, newer.StatusCode);
        var active = (await Json(newer)).GetProperty("workflowVersion").GetGuid();
        await WaitFor(() => HasVersion(server.Versions, seed.Request.WorkflowId!, active));
        await server.Versions.StopAsync(TestContext.Current.CancellationToken);
        var key = Guid.NewGuid().ToString("D");
        const string body = """{"variables":{"environment":"private-clone-value"}}""";
        using var accepted = await CloneRaw(server, seed.Snapshot.RunId, body, bearer, key);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var runId = (await Json(accepted)).GetProperty("runId").GetGuid();
        using var replay = await CloneRaw(server, seed.Snapshot.RunId, body, bearer, key);
        Assert.Equal(runId, (await Json(replay)).GetProperty("runId").GetGuid());
        Assert.Equal(1L, await Value("SELECT count(*) FROM commands WHERE run_id=$1", runId));
        Assert.Equal(1L, await Value("SELECT count(*) FROM audit_log WHERE action='run.clone' AND target_id=$1", runId.ToString("D")));
        Assert.DoesNotContain("private-clone-value",
            (string)(await Value("SELECT summary::text FROM audit_log WHERE action='run.clone' AND target_id=$1", runId.ToString("D")))!);
        using (var pending = await server.Send(HttpMethod.Get, $"/api/v1/runs/{runId:D}", null!, bearer))
        {
            Assert.Equal(HttpStatusCode.OK, pending.StatusCode);
            var snapshot = await Json(pending);
            Assert.Equal(seed.Version, snapshot.GetProperty("versionId").GetGuid());
            Assert.Equal(seed.Snapshot.RunId, snapshot.GetProperty("parentRunId").GetGuid());
            Assert.Empty(snapshot.GetProperty("reusedNodes").EnumerateObject());
            Assert.All(snapshot.GetProperty("nodes").EnumerateObject(), node => Assert.Equal((int)ExecutionStatus.Pending, node.Value.GetInt32()));
        }
        using var leader = new LeaseManager(database.DataSource,
            new(Guid.NewGuid().ToString("N"), new Uri("http://localhost:5263")), NullLogger<LeaseManager>.Instance);
        Assert.True(await leader.TryAcquireOrRenewAsync(TestContext.Current.CancellationToken));
        try
        {
            var store = new RunStore(database.DataSource, NullLogger<RunStore>.Instance, leader);
            using var recovery = new RunRecovery(database.DataSource, store, leader, server.Clock, NullLogger<RunRecovery>.Instance);
            await recovery.RecoverAsync(TestContext.Current.CancellationToken);
            await recovery.RecoverAsync(TestContext.Current.CancellationToken);
            var snapshot = (await store.LoadAsync(runId, TestContext.Current.CancellationToken)).Snapshot;
            Assert.Equal("private-clone-value", snapshot.Variables["environment"].GetString());
            Assert.Equal("rerun-original", snapshot.Metadata.Values["area"]);
            Assert.All(snapshot.Nodes.Values, state => Assert.Equal(ExecutionStatus.Pending, state));
            Assert.Equal(0L, await Value("SELECT count(*) FROM node_runs WHERE run_id=$1 AND reused_from_run_id IS NOT NULL", runId));
            Assert.Equal(0L, await Value("SELECT count(*) FROM task_attempts WHERE run_id=$1", runId));
            Assert.Equal((short)RunTrigger.Rerun, await Value("SELECT trigger FROM runs WHERE id=$1", runId));
        }
        finally { await leader.StopAsync(TestContext.Current.CancellationToken); }
        using var after = await server.Send(HttpMethod.Get, $"/api/v1/runs/{seed.Snapshot.RunId:D}", null!, bearer);
        Assert.Equal(original, await after.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(active, await Value("SELECT active_version_id FROM workflows WHERE id=$1", seed.Request.WorkflowId!));
        using var changed = await CloneRaw(server, seed.Snapshot.RunId, """{"variables":{"environment":"other"}}""", bearer, key);
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        await Execute("DELETE FROM role_bindings WHERE principal_id=$1", seed.Group);
        using var revoked = await CloneRaw(server, seed.Snapshot.RunId, body, bearer, key);
        Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
        using var revokedRead = await server.Send(HttpMethod.Get, $"/api/v1/runs/{seed.Snapshot.RunId:D}/clone", null!, bearer);
        Assert.Equal(HttpStatusCode.Forbidden, revokedRead.StatusCode);
        Assert.Empty(server.Failures);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("""{"variables":null}""")]
    [InlineData("""{"variables":[]}""")]
    [InlineData("""{"variables":{"unknown":"do-not-echo"}}""")]
    [InlineData("""{"variables":{"environment":42}}""")]
    [InlineData("""{"variables":{"environment":"nul\u0000value"}}""")]
    [InlineData("""{"variables":{},"fromNodes":["build"]}""")]
    [InlineData("""{"variables":{},"version":"2.0.0"}""")]
    [InlineData("""{"variables":{"environment":"one","environment":"two"}}""")]
    public async Task Clone_rejects_invalid_edits_without_commands_audit_or_reserved_keys(string body)
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var seed = await SeedRerun(server);
        var key = Guid.NewGuid().ToString("D");
        using var response = await CloneRaw(server, seed.Snapshot.RunId, body, RerunOperator(server, seed.Group), key);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("do-not-echo", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0L, await Value("SELECT count(*) FROM idempotency_keys WHERE key=$1", key));
        Assert.Equal(0L, await Value("SELECT count(*) FROM audit_log WHERE action='run.clone'"));
        Assert.Equal(0L, await Value("SELECT count(*) FROM commands WHERE payload->>'ParentRunId'=$1", seed.Snapshot.RunId.ToString("D")));
        Assert.Empty(server.Failures);
    }

    [Theory]
    [InlineData(RegistryRole.Viewer)]
    [InlineData(RegistryRole.Author)]
    public async Task Clone_source_and_acceptance_require_current_Run_Operator(RegistryRole role)
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var seed = await SeedRerun(server);
        await Execute("UPDATE role_bindings SET role=$2 WHERE principal_id=$1", seed.Group, (short)role);
        var bearer = RerunOperator(server, seed.Group);
        using var read = await server.Send(HttpMethod.Get, $"/api/v1/runs/{seed.Snapshot.RunId:D}/clone", null!, bearer);
        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.DoesNotContain("ppe", await read.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        using var write = await CloneRaw(server, seed.Snapshot.RunId, """{"variables":{}}""", bearer);
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
        Assert.Empty(server.Failures);
    }

    [Fact]
    public async Task Clone_nonterminal_source_and_invalid_transport_fail_without_acceptance()
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var seed = await SeedRerun(server, terminal: false);
        var bearer = RerunOperator(server, seed.Group);
        using var read = await server.Send(HttpMethod.Get, $"/api/v1/runs/{seed.Snapshot.RunId:D}/clone", null!, bearer);
        Assert.Equal(HttpStatusCode.Conflict, read.StatusCode);
        using var write = await CloneRaw(server, seed.Snapshot.RunId, """{"variables":{}}""", bearer);
        Assert.Equal(HttpStatusCode.Conflict, write.StatusCode);
        using var media = await server.Send(HttpMethod.Post, $"/api/v1/runs/{seed.Snapshot.RunId:D}/clone",
            new StringContent("""{"variables":{}}""", Encoding.UTF8, "text/plain"), bearer);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, media.StatusCode);
        using var large = await CloneRaw(server, seed.Snapshot.RunId,
            "{\"variables\":{\"environment\":\"" + new string('x', 1024 * 1024) + "\"}}", bearer);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, large.StatusCode);
        using var query = await server.Send(HttpMethod.Get, $"/api/v1/runs/{seed.Snapshot.RunId:D}/clone?extra=1", null!, bearer);
        Assert.Equal(HttpStatusCode.BadRequest, query.StatusCode);
        Assert.Empty(server.Failures);
    }

    private static Task<HttpResponseMessage> CloneRaw(Server server, Guid parent, string body, string bearer, string? key = null) =>
        server.Send(HttpMethod.Post, $"/api/v1/runs/{parent:D}/clone",
            new StringContent(body, Encoding.UTF8, "application/json"), bearer, idempotencyKeys: key is null ? null : [key]);
}
