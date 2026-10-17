using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Contracts.Manifest;
using Wf.Contracts.Registration;
using Wf.Contracts.Registry;
using Wf.Contracts.Runs;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Leadership;
using Wf.ControlPlane.Engine.Recovery;
using Wf.Engine.Core;
using Wf.Engine.Core.Scheduling;
using Wf.Sdk;
using Wf.Sdk.Local;

namespace Wf.Integration.Tests.Registration;

public sealed partial class RegistrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rerun_http_accepts_actual_failed_SDK_outputs_then_fenced_recovery_materializes_exact_pin_and_lineage(bool selectBuild)
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var seed = await SeedRerun(server);
        var manifest = seed.Request.Manifest!.Value.Deserialize(WorkflowManifestJsonContext.Default.WorkflowManifest)!;
        using var newer = await server.Post(seed.Request with
        {
            Manifest = JsonSerializer.SerializeToElement(manifest with
            {
                Version = "2.0.0",
                Metadata = new(manifest.Metadata.Type, manifest.Metadata.Values.SetItem("area", "new-registration")),
                Variables = [new("environment", VariableType.String, true, JsonSerializer.SerializeToElement("new-default"))]
            }, WorkflowManifestJsonContext.Default.WorkflowManifest)
        });
        Assert.Equal(HttpStatusCode.Created, newer.StatusCode);
        var latest = (await Json(newer)).GetProperty("workflowVersion").GetGuid();
        await WaitFor(() => HasVersion(server.Versions, seed.Request.WorkflowId!, latest));
        await server.Versions.StopAsync(TestContext.Current.CancellationToken);
        var key = Guid.NewGuid().ToString("D");
        var bearer = RerunOperator(server, seed.Group);
        var body = new RerunRunRequest(selectBuild ? ["build"] : null);
        var accepted = await RerunAccepted(server, seed.Snapshot.RunId, body, bearer, key);
        Assert.Equal(accepted, await RerunAccepted(server, seed.Snapshot.RunId, body, bearer, key));
        Assert.Equal(0L, await Value("SELECT count(*) FROM runs WHERE id=$1", accepted));
        Assert.Equal(1L, await Value("SELECT count(*) FROM commands WHERE run_id=$1 AND kind=0", accepted));
        Assert.Equal(1L, await Value("SELECT count(*) FROM audit_log WHERE action='run.rerun' AND target_id=$1", accepted.ToString("D")));
        using (var pending = await server.Send(HttpMethod.Get, $"/api/v1/runs/{accepted:D}", null!, bearer))
        {
            Assert.Equal(HttpStatusCode.OK, pending.StatusCode);
            var snapshot = await Json(pending);
            Assert.Equal(seed.Version, snapshot.GetProperty("versionId").GetGuid());
            Assert.Equal(seed.Snapshot.RunId, snapshot.GetProperty("parentRunId").GetGuid());
            Assert.Equal(selectBuild ? 0 : 2, snapshot.GetProperty("reusedNodes").EnumerateObject().Count());
            if (!selectBuild)
                Assert.Equal(seed.Snapshot.RunId, snapshot.GetProperty("reusedNodes").GetProperty("build").GetGuid());
            Assert.Equal((int)RunStatus.Queued, snapshot.GetProperty("status").GetInt32());
            Assert.Equal((int)(selectBuild ? ExecutionStatus.Pending : ExecutionStatus.Reused),
                snapshot.GetProperty("nodes").GetProperty("build").GetInt32());
        }
        using var leader = new LeaseManager(database.DataSource,
            new(Guid.NewGuid().ToString("N"), new Uri("http://localhost:5261")), NullLogger<LeaseManager>.Instance);
        Assert.True(await leader.TryAcquireOrRenewAsync(TestContext.Current.CancellationToken));
        try
        {
            var store = new RunStore(database.DataSource, NullLogger<RunStore>.Instance, leader);
            using var recovery = new RunRecovery(database.DataSource, store, leader, server.Clock, NullLogger<RunRecovery>.Instance);
            await recovery.RecoverAsync(TestContext.Current.CancellationToken);
            await recovery.RecoverAsync(TestContext.Current.CancellationToken);
            var state = await store.LoadAsync(accepted, TestContext.Current.CancellationToken);
            Assert.Equal("ppe", state.Snapshot.Variables["environment"].GetString());
            Assert.Equal("rerun-original", state.Snapshot.Metadata.Values["area"]);
            Assert.Equal(seed.Snapshot.Metadata.Type, state.Snapshot.Metadata.Type);
            Assert.Equal(seed.Snapshot.Metadata.Values.ToArray(), state.Snapshot.Metadata.Values.ToArray());
            Assert.Equal(seed.Version, await Value("SELECT version_id FROM runs WHERE id=$1", accepted));
            Assert.Equal(seed.Snapshot.RunId, await Value("SELECT parent_run_id FROM runs WHERE id=$1", accepted));
            Assert.Equal((short)RunTrigger.Rerun, await Value("SELECT trigger FROM runs WHERE id=$1", accepted));
            Assert.Equal(0L, await Value("SELECT count(*) FROM task_attempts WHERE run_id=$1", accepted));
            Assert.Equal(selectBuild ? ["build"] : ["deploy-us"],
                ReadinessEvaluator.Evaluate(manifest.Nodes, state.Snapshot.Nodes).Select(change => change.Node));
            Assert.Equal(selectBuild ? 0L : 2L, await Value(
                "SELECT count(*) FROM node_runs WHERE run_id=$1 AND reused_from_run_id=$2", accepted, seed.Snapshot.RunId));
            if (!selectBuild)
            {
                Assert.Equal("package-v1.0.0", await Value(
                    "SELECT outputs->>'artifact' FROM node_runs WHERE run_id=$1 AND node_key='build'", accepted));
                Assert.Equal(long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture), await Value(
                    "SELECT outputs->>'limit' FROM node_runs WHERE run_id=$1 AND node_key='build'", accepted));
                Assert.Equal("https://eu.invalid/v1", await Value(
                    "SELECT outputs->>'endpoint' FROM node_runs WHERE run_id=$1 AND node_key='deploy-eu'", accepted));
            }
            Assert.Equal(seed.Version, await Value("SELECT version_id FROM runs WHERE id=$1", seed.Snapshot.RunId));
            Assert.Equal(latest, await Value("SELECT active_version_id FROM workflows WHERE id=$1", seed.Request.WorkflowId!));
            Assert.Equal(accepted, await RerunAccepted(server, seed.Snapshot.RunId, body, bearer, key));
            Assert.Equal(1L, await Value("SELECT count(*) FROM runs WHERE id=$1", accepted));
            using var read = await server.Send(HttpMethod.Get, $"/api/v1/runs/{accepted:D}", null!, bearer);
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            var committed = await Json(read);
            Assert.Equal(seed.Snapshot.RunId, committed.GetProperty("parentRunId").GetGuid());
            Assert.Equal(selectBuild ? 0 : 2, committed.GetProperty("reusedNodes").EnumerateObject().Count());
            if (!selectBuild)
            {
                Assert.Equal(seed.Snapshot.RunId, committed.GetProperty("reusedNodes").GetProperty("build").GetGuid());
                Assert.Equal(seed.Snapshot.RunId, committed.GetProperty("reusedNodes").GetProperty("deploy-eu").GetGuid());
            }
        }
        finally { await leader.StopAsync(TestContext.Current.CancellationToken); }
        Assert.Empty(server.Failures);
    }

    [Theory]
    [InlineData("snapshot-status")]
    [InlineData("reusable-outputs")]
    public async Task Rerun_corrupt_committed_source_fails_closed_without_reserving_key_or_leaking_outputs(string invalid)
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var seed = await SeedRerun(server);
        if (invalid == "snapshot-status")
            await Execute("UPDATE runs SET status=$2 WHERE id=$1", seed.Snapshot.RunId, (short)RunStatus.Succeeded);
        else
            await Execute("UPDATE node_runs SET outputs='[\"must-not-echo\"]'::jsonb WHERE run_id=$1 AND node_key='build'",
                seed.Snapshot.RunId);
        var key = Guid.NewGuid().ToString("D");
        using var response = await RerunSend(server, seed.Snapshot.RunId, new(), RerunOperator(server, seed.Group), key);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("must-not-echo", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0L, await Value("SELECT count(*) FROM idempotency_keys WHERE key=$1", key));
        Assert.Equal(0L, await Value("SELECT count(*) FROM commands WHERE payload->>'ParentRunId'=$1", seed.Snapshot.RunId.ToString("D")));
        Assert.Empty(server.Failures);
    }

    [Fact]
    public async Task Rerun_idempotency_serializes_concurrent_hosts_and_canonical_node_sets_but_conflicts_on_parent_or_selection()
    {
        await using var first = await Server.Start(database.DataSource, runs: true);
        await using var second = await Server.Start(database.DataSource, runs: true);
        var seed = await SeedRerun(first);
        var key = Guid.NewGuid().ToString("D");
        var responses = await Task.WhenAll(Enumerable.Range(0, 24).Select(index =>
        {
            var server = index % 2 == 0 ? first : second;
            return RerunAccepted(server, seed.Snapshot.RunId,
                new(index % 2 == 0 ? ["build", "deploy-us"] : ["deploy-us", "build"]), RerunOperator(server, seed.Group), key);
        }));
        var accepted = Assert.Single(responses.Distinct());
        Assert.Equal(1L, await Value("SELECT count(*) FROM commands WHERE run_id=$1 AND kind=0", accepted));
        Assert.Equal(1L, await Value("SELECT count(*) FROM audit_log WHERE action='run.rerun' AND target_id=$1", accepted.ToString("D")));
        using var changed = await RerunSend(first, seed.Snapshot.RunId, new(["deploy-us"]), RerunOperator(first, seed.Group), key);
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        var other = await SeedRerun(first);
        using var parent = await RerunSend(first, other.Snapshot.RunId, new(["build", "deploy-us"]), first.Token("Admin"), key);
        Assert.Equal(HttpStatusCode.Conflict, parent.StatusCode);
        await Execute("DELETE FROM role_bindings WHERE principal_id=$1", seed.Group);
        using var revoked = await RerunSend(first, seed.Snapshot.RunId, new(["build", "deploy-us"]), RerunOperator(first, seed.Group), key);
        Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
        Assert.DoesNotContain(accepted.ToString("D"), await revoked.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Empty(first.Failures);
        Assert.Empty(second.Failures);
    }

    [Theory]
    [InlineData(RegistryRole.Viewer, false, 403)]
    [InlineData(RegistryRole.Author, false, 403)]
    [InlineData(RegistryRole.Operator, false, 202)]
    [InlineData(RegistryRole.Admin, false, 202)]
    [InlineData(RegistryRole.Operator, true, 403)]
    public async Task Rerun_requires_current_owning_Run_Operator_and_rejects_application_identity(RegistryRole role, bool app, int status)
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var seed = await SeedRerun(server);
        await Execute("UPDATE role_bindings SET role=$2 WHERE principal_id=$1", seed.Group, (short)role);
        var token = server.Token(role.ToString(), app ? "app" : "user", app ? null : "api.read", groups: [seed.Group]);
        using var response = await RerunSend(server, seed.Snapshot.RunId, new(), token);
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(status == 202 ? 1L : 0L, await Value("SELECT count(*) FROM commands WHERE payload->>'ParentRunId'=$1",
            seed.Snapshot.RunId.ToString("D")));
        Assert.Empty(server.Failures);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"fromNodes\":null}")]
    public async Task Rerun_nonterminal_source_returns_409_without_reserving_idempotency_or_inserting_command(string json)
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var seed = await SeedRerun(server, terminal: false);
        var key = Guid.NewGuid().ToString("D");
        using var response = await RerunRaw(server, seed.Snapshot.RunId.ToString("D"), json, RerunOperator(server, seed.Group), key);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(0L, await Value("SELECT count(*) FROM idempotency_keys WHERE key=$1", key));
        Assert.Equal(0L, await Value("SELECT count(*) FROM commands WHERE payload->>'ParentRunId'=$1", seed.Snapshot.RunId.ToString("D")));
        Assert.Empty(server.Failures);
    }

    [Theory]
    [InlineData("null", 400)]
    [InlineData("[]", 400)]
    [InlineData("{\"fromNodes\":[]}", 400)]
    [InlineData("{\"fromNodes\":[\"\"]}", 400)]
    [InlineData("{\"fromNodes\":[null]}", 400)]
    [InlineData("{\"fromNodes\":[\"build\",\"build\"]}", 400)]
    [InlineData("{\"fromNodes\":[\"unknown\"]}", 400)]
    [InlineData("{\"fromNodes\":[],\"fromNodes\":[]}", 400)]
    [InlineData("{\"variables\":{\"private\":\"do-not-echo\"}}", 400)]
    [InlineData("{\"version\":\"2.0.0\"}", 400)]
    [InlineData("{\"fromNodes\":42}", 400)]
    [InlineData("{\"workflowId\":\"another/type/workflow\"}", 400)]
    public async Task Rerun_rejects_invalid_or_mutating_request_shape_without_writing(string body, int status)
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var seed = await SeedRerun(server);
        var key = Guid.NewGuid().ToString("D");
        using var response = await RerunRaw(server, seed.Snapshot.RunId.ToString("D"), body, RerunOperator(server, seed.Group), key);
        Assert.Equal(status, (int)response.StatusCode);
        Assert.DoesNotContain("do-not-echo", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0L, await Value("SELECT count(*) FROM idempotency_keys WHERE key=$1", key));
        Assert.Equal(0L, await Value("SELECT count(*) FROM commands WHERE payload->>'ParentRunId'=$1", seed.Snapshot.RunId.ToString("D")));
        Assert.Empty(server.Failures);
    }

    [Theory]
    [InlineData("uppercase", 400)]
    [InlineData("query", 400)]
    [InlineData("zero", 400)]
    [InlineData("v4", 400)]
    [InlineData("large", 413)]
    [InlineData("media", 415)]
    [InlineData("missing", 403)]
    [InlineData("anonymous", 401)]
    [InlineData("actor", 403)]
    public async Task Rerun_validates_transport_exact_identity_and_principal_before_acceptance(string invalid, int status)
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var seed = await SeedRerun(server);
        var id = invalid switch
        {
            "uppercase" => seed.Snapshot.RunId.ToString("D").ToUpperInvariant(),
            "query" => seed.Snapshot.RunId.ToString("D") + "/rerun?extra=1",
            "zero" => Guid.Empty.ToString("D"),
            "v4" => Guid.NewGuid().ToString("D"),
            "missing" => Guid.CreateVersion7().ToString("D"),
            _ => seed.Snapshot.RunId.ToString("D")
        };
        var body = invalid == "large" ? "{\"fromNodes\":[\"" + new string('x', 65536) + "\"]}" : "{}";
        var bearer = invalid == "anonymous" ? null : invalid == "actor" ? server.Token("Admin", includeActor: false) : server.Token("Admin");
        using var response = await server.Send(HttpMethod.Post, $"/api/v1/runs/{id}" + (invalid == "query" ? "" : "/rerun"),
            new StringContent(body, Encoding.UTF8, invalid == "media" ? "text/plain" : "application/json"),
            bearer, useDefaultToken: false);
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(0L, await Value("SELECT count(*) FROM commands WHERE payload->>'ParentRunId'=$1", seed.Snapshot.RunId.ToString("D")));
        Assert.Empty(server.Failures);
    }

    [Fact]
    public async Task Rerun_pending_run_can_be_aborted_and_idempotency_rollback_leaves_no_command_audit_or_notification()
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var seed = await SeedRerun(server);
        await using var listener = await database.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using (var listen = new NpgsqlCommand("LISTEN commands", listener))
            await listen.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        var notifications = new List<string>();
        listener.Notification += (_, notification) => notifications.Add(notification.Payload);
        await Execute("ALTER TABLE audit_log ADD CONSTRAINT reject_test_rerun CHECK(action <> 'run.rerun') NOT VALID");
        var key = Guid.NewGuid().ToString("D");
        try
        {
            using var failure = await RerunSend(server, seed.Snapshot.RunId, new(), server.Token("Admin"), key);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, failure.StatusCode);
            Assert.Equal(0L, await Value("SELECT count(*) FROM idempotency_keys WHERE key=$1", key));
            Assert.Equal(0L, await Value("SELECT count(*) FROM commands WHERE payload->>'ParentRunId'=$1", seed.Snapshot.RunId.ToString("D")));
            await listener.WaitAsync(100, TestContext.Current.CancellationToken);
            Assert.Empty(notifications);
        }
        finally { await Execute("ALTER TABLE audit_log DROP CONSTRAINT reject_test_rerun"); }
        var accepted = await RerunAccepted(server, seed.Snapshot.RunId, new(), server.Token("Admin"), key);
        using var abort = await server.Send(HttpMethod.Post, $"/api/v1/runs/{accepted:D}/abort",
            new StringContent("{\"reason\":\"Cancel accepted rerun\",\"source\":\"API\"}", Encoding.UTF8, "application/json"),
            RerunOperator(server, seed.Group));
        Assert.Equal(HttpStatusCode.Accepted, abort.StatusCode);
        Assert.Equal(1L, await Value("SELECT count(DISTINCT created_at) FROM commands WHERE run_id=$1", accepted));
        using var leader = new LeaseManager(database.DataSource,
            new(Guid.NewGuid().ToString("N"), new Uri("http://localhost:5262")), NullLogger<LeaseManager>.Instance);
        Assert.True(await leader.TryAcquireOrRenewAsync(TestContext.Current.CancellationToken));
        try
        {
            var store = new RunStore(database.DataSource, NullLogger<RunStore>.Instance, leader);
            using var recovery = new RunRecovery(database.DataSource, store, leader, server.Clock, NullLogger<RunRecovery>.Instance);
            await recovery.RecoverAsync(TestContext.Current.CancellationToken);
            Assert.Equal(RunStatus.Aborted, (await store.LoadAsync(accepted, TestContext.Current.CancellationToken)).Snapshot.Status);
            Assert.Equal(2L, await Value("SELECT count(*) FROM commands WHERE run_id=$1 AND processed_at IS NOT NULL", accepted));
        }
        finally { await leader.StopAsync(TestContext.Current.CancellationToken); }
        Assert.Empty(server.Failures);
    }

    private sealed record RerunSeed(RegistrationRequest Request, Guid Version, RunSnapshot Snapshot, Guid Group);

    [Theory]
    [InlineData("missing-source")]
    [InlineData("extra-source")]
    [InlineData("self-source")]
    [InlineData("missing-parent")]
    [InlineData("invalid-source")]
    public async Task Rerun_snapshot_rejects_corrupt_lineage_without_exposing_original_outputs(string corruption)
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var seed = await SeedRerun(server);
        var bearer = RerunOperator(server, seed.Group);
        var child = await RerunAccepted(server, seed.Snapshot.RunId, new(null), bearer);
        using var leader = new LeaseManager(database.DataSource,
            new(Guid.NewGuid().ToString("N"), new Uri("http://localhost:5261")), NullLogger<LeaseManager>.Instance);
        Assert.True(await leader.TryAcquireOrRenewAsync(TestContext.Current.CancellationToken));
        try
        {
            var store = new RunStore(database.DataSource, NullLogger<RunStore>.Instance, leader);
            using var recovery = new RunRecovery(database.DataSource, store, leader, server.Clock, NullLogger<RunRecovery>.Instance);
            await recovery.RecoverAsync(TestContext.Current.CancellationToken);
            switch (corruption)
            {
                case "missing-source":
                    await Execute("UPDATE node_runs SET reused_from_run_id=NULL WHERE run_id=$1 AND node_key='build'", child);
                    break;
                case "extra-source":
                    await Execute("UPDATE node_runs SET reused_from_run_id=$2 WHERE run_id=$1 AND node_key='deploy-us'", child, seed.Snapshot.RunId);
                    break;
                case "self-source":
                    await Execute("UPDATE node_runs SET reused_from_run_id=$1 WHERE run_id=$1 AND node_key='build'", child);
                    break;
                case "missing-parent":
                    await Execute("UPDATE runs SET parent_run_id=NULL WHERE id=$1", child);
                    break;
                case "invalid-source":
                    await Execute("UPDATE node_runs SET reused_from_run_id=$2 WHERE run_id=$1 AND node_key='build'", child, Guid.NewGuid());
                    break;
                default: Assert.Fail("Unknown lineage corruption."); break;
            }
            using var response = await server.Send(HttpMethod.Get, $"/api/v1/runs/{child:D}", null!, bearer);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.Equal("run_data_inconsistent", (await Json(response)).GetProperty("reason").GetString());
            Assert.DoesNotContain("package-v1.0.0", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.True(response.Headers.CacheControl!.NoStore);
        }
        finally { await leader.StopAsync(TestContext.Current.CancellationToken); }
        Assert.Empty(server.Failures);
    }

    private async Task<RerunSeed> SeedRerun(Server server, bool terminal = true, Guid? viewerGroup = null)
    {
        var request = await Request(server);
        var workflow = Workflow.Define(request.WorkflowId!, "1.0.0", w => w.Type("release").Metadata("area", "rerun-original")
            .Variables(variables => variables.String("environment", "dev"))
            .Node("build", node => node.Task("compile", context =>
            {
                context.Outputs["artifact"] = "package-v1.0.0";
                context.Outputs["limit"] = long.MaxValue;
            }))
            .Node("deploy-eu", node => node.DependsOn("build").Task("deploy", context => context.Outputs["endpoint"] = "https://eu.invalid/v1"))
            .Node("deploy-us", node => node.DependsOn("deploy-eu").Task("deploy", _ => throw new InvalidOperationException("US failed")))
            .Node("verify", node => node.DependsOn("deploy-us").Task("check", _ => { })));
        using var manifest = JsonDocument.Parse(ManifestWriter.WriteUtf8(workflow));
        var registry = request.Registry!.Value.Deserialize(TeamRegistryJsonContext.Default.TeamRegistry)!;
        var group = Guid.NewGuid();
        request = request with
        {
            Manifest = manifest.RootElement.Clone(),
            Registry = JsonSerializer.SerializeToElement(registry with
            {
                Types = [registry.Types[0] with
                {
                    MetadataSchema = new([new("area")]),
                    RoleBindings = viewerGroup is { } viewer
                        ? [new(RegistryRole.Operator, group), new(RegistryRole.Viewer, viewer)]
                        : [new(RegistryRole.Operator, group)]
                }]
            }, TeamRegistryJsonContext.Default.TeamRegistry)
        };
        using var registered = await server.Post(request);
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        var version = (await Json(registered)).GetProperty("workflowVersion").GetGuid();
        var variables = ImmutableDictionary<string, JsonElement>.Empty.Add("environment", JsonSerializer.SerializeToElement("ppe"));
        LocalRunResult? result = terminal
            ? await new LocalEngine(workflow, variables, TextWriter.Null).RunAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken) : null;
        var state = new RunState(result?.Snapshot.RunId ?? Guid.CreateVersion7(), workflow.Nodes, workflow.Metadata, Assert.Fail, variables);
        var store = new RunStore(database.DataSource, NullLogger<RunStore>.Instance);
        await store.CreateAsync(state, request.WorkflowId!, version, DateTimeOffset.UtcNow.AddHours(1), TestContext.Current.CancellationToken);
        if (result is not null)
        {
            Assert.Equal(RunStatus.Failed, result.Snapshot.Status);
            foreach (var change in result.Events)
                await store.AppendAsync(state, change, TestContext.Current.CancellationToken);
            foreach (var (node, outputs) in result.Outputs)
                await Execute("UPDATE node_runs SET outputs=$3::jsonb WHERE run_id=$1 AND node_key=$2",
                    state.Snapshot.RunId, node, JsonSerializer.Serialize(outputs));
        }
        return new(request, version, state.Snapshot, group);
    }

    private static string RerunOperator(Server server, Guid group) => server.Token("", "user", "api.read", groups: [group]);

    private static Task<HttpResponseMessage> RerunSend(Server server, Guid parent, RerunRunRequest request,
        string bearer, string? key = null) =>
        server.Send(HttpMethod.Post, $"/api/v1/runs/{parent:D}/rerun",
            JsonContent.Create(request, RunJsonContext.Default.RerunRunRequest), bearer,
            idempotencyKeys: key is null ? null : [key]);

    private static Task<HttpResponseMessage> RerunRaw(Server server, string parent, string body, string bearer, string? key = null) =>
        server.Send(HttpMethod.Post, $"/api/v1/runs/{parent}/rerun", new StringContent(body, Encoding.UTF8, "application/json"),
            bearer, idempotencyKeys: key is null ? null : [key]);

    private static async Task<Guid> RerunAccepted(Server server, Guid parent, RerunRunRequest body, string bearer, string? key = null)
    {
        using var response = await RerunSend(server, parent, body, bearer, key);
        Assert.True(response.StatusCode == HttpStatusCode.Accepted,
            $"Expected 202, got {(int)response.StatusCode}. {await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        return (await Json(response)).GetProperty("runId").GetGuid();
    }
}
