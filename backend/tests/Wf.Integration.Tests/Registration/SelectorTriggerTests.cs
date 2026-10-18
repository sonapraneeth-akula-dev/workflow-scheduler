using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Wf.Contracts.Definition;
using Wf.Contracts.Manifest;
using Wf.Contracts.Registration;
using Wf.Contracts.Runs;
using Wf.Sdk;
using static Wf.Integration.Tests.Registration.RegistrationTests;

namespace Wf.Integration.Tests.Registration;

public sealed partial class TriggerRunTests
{
    [Theory]
    [InlineData("unknown-id")]
    [InlineData("id-os")]
    [InlineData("label")]
    [InlineData("count")]
    [InlineData("pool")]
    [InlineData("name")]
    public async Task Selectors_registration_rejects_unsatisfiable_configuration_before_any_registry_version_or_audit_write(string kind)
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var machine = await SeedSelectorMachine(database.DataSource);
        var unknown = Guid.NewGuid();
        var selector = kind switch
        {
            "unknown-id" => Machines.Ids(machine, unknown),
            "id-os" => Machines.All(Machines.Ids(machine), Os.Windows),
            "label" => Machines.All(Machines.Ids(machine), Machines.Label("missing-key")),
            "count" => Machines.All(Machines.Pool(machine.ToString("N")), Machines.Count(2)),
            "pool" => Machines.Pool("never-echo-" + unknown),
            "name" => Machines.Named("never-echo-" + unknown),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        var request = WithSelector(await Request(server), selector);
        using var response = await server.Post(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = Assert.Single((await Json(response)).GetProperty("errors").EnumerateArray());
        Assert.Equal("WFV027", error.GetProperty("ruleId").GetString());
        Assert.Equal("$.nodes[0].tasks[0].placement.selector", error.GetProperty("path").GetString());
        Assert.Contains("work/task", error.GetProperty("hint").GetString());
        if (kind == "unknown-id") Assert.Contains(unknown.ToString("D"), error.GetProperty("hint").GetString());
        if (kind == "label") Assert.Contains("missing-key", error.GetProperty("hint").GetString());
        Assert.DoesNotContain("never-echo-", await response.Content.ReadAsStringAsync(Token));
        Assert.Equal(0L, await Scalar("SELECT count(*) FROM workflow_versions WHERE workflow_id=$1", request.WorkflowId!));
        Assert.Equal(0L, await Scalar("SELECT count(*) FROM audit_log WHERE target_id=$1", request.WorkflowId!));
        Assert.Equal(0L, await Scalar("SELECT count(*) FROM teams WHERE id=$1", request.WorkflowId!.Split('/')[0]));
        Assert.Empty(server.Failures);
    }

    [Fact]
    public async Task Selectors_trigger_binds_symbolic_values_checks_fresh_inventory_and_rolls_back_failed_idempotency_claim()
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var machine = await SeedSelectorMachine(database.DataSource);
        var request = WithSelector(await Request(server), Machines.All(Machines.Ids(machine),
            Os.Linux, Machines.Label("role", new(variable: "role"))));
        var manifest = request.Manifest!.Value.Deserialize(WorkflowManifestJsonContext.Default.WorkflowManifest)! with
        {
            Variables = [new("role", VariableType.String, true)]
        };
        request = request with { Manifest = JsonSerializer.SerializeToElement(manifest, WorkflowManifestJsonContext.Default.WorkflowManifest) };
        var group = Guid.NewGuid();
        var version = await Register(server, request, group);
        var bearer = Operator(server, group);
        var key = Guid.NewGuid().ToString("N");
        var body = new TriggerRunRequest(request.WorkflowId!)
        {
            Variables = ImmutableDictionary<string, JsonElement>.Empty.Add("role", Element("\"never-echo-this-value\""))
        };
        using (var rejected = await server.Send(HttpMethod.Post, "/api/v1/runs",
            JsonContent.Create(body, RunJsonContext.Default.TriggerRunRequest), bearer, idempotencyKeys: [key]))
        {
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            Assert.Contains("role", (await Json(rejected)).GetProperty("errors")[0].GetProperty("hint").GetString());
            Assert.DoesNotContain("never-echo-this-value", await rejected.Content.ReadAsStringAsync(Token));
        }
        Assert.Equal(0L, await Scalar("SELECT count(*) FROM idempotency_keys WHERE key=$1", key));
        Assert.Equal(0L, await Scalar("SELECT count(*) FROM commands WHERE payload->>'WorkflowId'=$1", request.WorkflowId!));
        body = body with { Variables = body.Variables.SetItem("role", Element("\"perf\"")) };
        using (var accepted = await server.Send(HttpMethod.Post, "/api/v1/runs",
            JsonContent.Create(body, RunJsonContext.Default.TriggerRunRequest), bearer, idempotencyKeys: [key]))
        {
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            var run = (await Json(accepted)).GetProperty("runId").GetGuid();
            Assert.Equal(version, (await Payload(run)).GetProperty("VersionId").GetGuid());
        }
        await Execute("UPDATE agents SET labels=ARRAY['role=changed'] WHERE id=$1", machine);
        using (var changed = await Trigger(server, body, bearer)) Assert.Equal(HttpStatusCode.BadRequest, changed.StatusCode);
        await Execute("DELETE FROM agents WHERE id=$1", machine);
        using (var missing = await Trigger(server, body, bearer))
        {
            Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
            Assert.Contains(machine.ToString("D"), (await Json(missing)).GetProperty("errors")[0].GetProperty("hint").GetString());
        }
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM commands WHERE payload->>'WorkflowId'=$1", request.WorkflowId!));
        Assert.Empty(server.Failures);
    }

    [Fact]
    public async Task Selectors_known_unhealthy_full_agents_are_valid_configuration_and_innermost_selector_wins()
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var machine = await SeedSelectorMachine(database.DataSource);
        await Execute("UPDATE agents SET status=2,slots_used=max_tasks,last_heartbeat_at=now()-interval '1 day' WHERE id=$1", machine);
        var request = WithSelector(await Request(server), Machines.All(Machines.Ids(machine), Os.Linux, Machines.Label("role", "perf")));
        var manifest = request.Manifest!.Value.Deserialize(WorkflowManifestJsonContext.Default.WorkflowManifest)! with
        {
            Placement = new(Machines.Ids(Guid.NewGuid()))
        };
        request = request with { Manifest = JsonSerializer.SerializeToElement(manifest, WorkflowManifestJsonContext.Default.WorkflowManifest) };
        var group = Guid.NewGuid();
        await Register(server, request, group);
        _ = await Accepted(server, new(request.WorkflowId!), Operator(server, group));
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM commands WHERE payload->>'WorkflowId'=$1", request.WorkflowId!));
        Assert.Empty(server.Failures);
    }

    [Fact]
    public async Task Selectors_schedule_run_now_checks_bound_instance_values_without_partial_commands_or_cursor_changes()
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var machine = await SeedSelectorMachine(database.DataSource);
        var request = WithSelector(await Request(server), Machines.All(Machines.Ids(machine),
            Machines.Label("role", new(variable: "role"))));
        var manifest = request.Manifest!.Value.Deserialize(WorkflowManifestJsonContext.Default.WorkflowManifest)! with
        {
            Variables = [new("role", VariableType.String, true)]
        };
        request = request with
        {
            Manifest = JsonSerializer.SerializeToElement(manifest, WorkflowManifestJsonContext.Default.WorkflowManifest),
            Schedules = """{"instances":[{"id":"timer","interval":"00:01:00","timezone":"Etc/UTC","variables":{"role":"perf"}}]}""",
            ScheduleSourcePath = "workflow.schedules.json"
        };
        var group = Guid.NewGuid();
        await Register(server, request, group);
        var schedule = Assert.IsType<Guid>(await Scalar("SELECT id FROM schedules WHERE workflow_id=$1", request.WorkflowId!));
        var xmin = await Scalar("SELECT xmin::text FROM schedules WHERE id=$1", schedule);
        await Execute("UPDATE agents SET labels=ARRAY['role=changed'] WHERE id=$1", machine);
        using (var rejected = await RunNow(server, schedule, Operator(server, group)))
        {
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            Assert.Equal("WFV027", (await Json(rejected)).GetProperty("errors")[0].GetProperty("ruleId").GetString());
        }
        Assert.Equal(0L, await Scalar("SELECT count(*) FROM commands WHERE payload->>'WorkflowId'=$1", request.WorkflowId!));
        await Execute("UPDATE agents SET labels=ARRAY['role=perf'] WHERE id=$1", machine);
        _ = await RunNowAccepted(server, schedule, Operator(server, group));
        Assert.Equal(xmin, await Scalar("SELECT xmin::text FROM schedules WHERE id=$1", schedule));
        Assert.Equal(0L, await Scalar("SELECT count(*) FROM schedule_fires WHERE schedule_id=$1", schedule));
        Assert.Empty(server.Failures);
    }

    [Fact]
    public async Task Selectors_legacy_manifest_without_empty_machine_IDs_remains_idempotent_without_rewriting_immutable_content()
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var machine = await SeedSelectorMachine(database.DataSource);
        var request = WithSelector(await Request(server), Machines.Pool(machine.ToString("N")));
        var group = Guid.NewGuid();
        var version = await Register(server, request, group);
        request = request with { Registry = null };
        Assert.True(Assert.IsType<bool>(await Scalar("""
            SELECT manifest #> '{nodes,0,tasks,0,placement,selector}' ? 'machineIds' FROM workflow_versions WHERE id=$1
            """, version)));
        await Execute("""
            UPDATE workflow_versions SET manifest=manifest #- '{nodes,0,tasks,0,placement,selector,machineIds}' WHERE id=$1
            """, version);
        Assert.False(Assert.IsType<bool>(await Scalar("""
            SELECT manifest #> '{nodes,0,tasks,0,placement,selector}' ? 'machineIds' FROM workflow_versions WHERE id=$1
            """, version)));
        var before = await Scalar("SELECT manifest::text FROM workflow_versions WHERE id=$1", version);
        var xmin = await Scalar("SELECT xmin::text FROM workflow_versions WHERE id=$1", version);
        using (var identical = await server.Post(request)) Assert.Equal(HttpStatusCode.OK, identical.StatusCode);
        Assert.Equal(before, await Scalar("SELECT manifest::text FROM workflow_versions WHERE id=$1", version));
        Assert.Equal(xmin, await Scalar("SELECT xmin::text FROM workflow_versions WHERE id=$1", version));
        using (var changed = await server.Post(WithSelector(request, Machines.Ids(machine))))
            Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        Assert.Equal(before, await Scalar("SELECT manifest::text FROM workflow_versions WHERE id=$1", version));
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM workflow_versions WHERE workflow_id=$1", request.WorkflowId!));
        Assert.Empty(server.Failures);
    }

    internal static RegistrationRequest WithSelector(RegistrationRequest request, PlacementSelector selector)
    {
        var manifest = request.Manifest!.Value.Deserialize(WorkflowManifestJsonContext.Default.WorkflowManifest)!;
        var node = manifest.Nodes[0];
        manifest = manifest with
        {
            Nodes = [new(node.Key, node.Dependencies, [new("task", placement: new(selector))])]
        };
        return request with { Manifest = JsonSerializer.SerializeToElement(manifest, WorkflowManifestJsonContext.Default.WorkflowManifest) };
    }

    internal static async Task<Guid> SeedSelectorMachine(NpgsqlDataSource source)
    {
        var id = Guid.NewGuid();
        await using var insert = source.CreateCommand("""
            INSERT INTO agents(id,name,os,arch,cores,ram_mb,max_tasks,labels,pool,status,last_heartbeat_at,agent_version,session_id)
            VALUES($1,$1::text,1,0,4,8192,2,ARRAY['role=perf'],$2,0,now(),'1.0',$1)
            """);
        insert.Parameters.AddWithValue(id);
        insert.Parameters.AddWithValue(id.ToString("N"));
        await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return id;
    }
}
