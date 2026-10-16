using System.Collections.Immutable;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Wf.Contracts;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Definition;
using Wf.Contracts.Events;
using Wf.Contracts.Registry;
using Wf.ControlPlane.Api;
using Wf.ControlPlane.Auth;
using Wf.ControlPlane.Data;
using Wf.ControlPlane.Engine.Agents;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Recovery;
using Wf.ControlPlane.Engine.Sweeper;
using Wf.ControlPlane.Registry;
using Wf.Engine.Core;
using Wf.Engine.Core.Placement;
using Wf.Integration.Tests.Agents;
using Wf.Sdk;
using Wf.Sdk.Local;
using DispatchCommand = Wf.Contracts.Agent.V1.Dispatch;

namespace Wf.Integration.Tests.Execution;

public sealed class RunTimelineApiTests(RunTimelineDatabase database, ITestOutputHelper output)
    : IClassFixture<RunTimelineDatabase>
{
    private const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private RunStore Store() => new(database.DataSource, NullLogger<RunStore>.Instance);

    [Fact]
    public Task Timeline_actual_dispatch_retry_preserves_each_durable_agent_and_recorded_active_and_terminal_timings() =>
        ActualDispatchRetry(browser: false);

    [Fact]
    [Trait("Category", "FrontendBrowser")]
    public async Task Timeline_actual_dispatch_retry_in_three_browsers_matches_authoritative_API()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("WF_TIMELINE_BROWSER") == "true",
            "Set WF_TIMELINE_BROWSER=true and WF_FRONTEND_ROOT after installing frontend dependencies and browsers.");
        await ActualDispatchRetry(browser: true);
    }

    private async Task ActualDispatchRetry(bool browser)
    {
        var (team, group) = await Registry();
        var store = Store();
        var version = Guid.CreateVersion7();
        var definition = Workflow.Define($"{team}/metrics/workflow", "1.0.0", w =>
            w.Type("metrics").Node("node", n => n.Task("task", _ => { })));
        await Version(definition, version);
        var run = await Create(team, version);
        var clock = new TimelineClock();
        using var registry = new AgentRegistry(database.DataSource, clock);
        var first = await registry.RegisterAsync(Hello(), Token);
        using var dispatcher = new Dispatcher(database.DataSource, store, registry, new PlacementEngine(), clock);
        using var sweeper = new AgentLossSweeper(database.DataSource, registry, clock, NullLogger<AgentLossSweeper>.Instance);
        await store.AppendAsync(run, EventEntity.Run, null, (int)RunStatus.Running, clock.GetUtcNow(), Token);
        foreach (var status in new[] { ExecutionStatus.Ready, ExecutionStatus.Dispatched, ExecutionStatus.Running })
            await store.AppendAsync(run, EventEntity.Node, "node", (int)status, clock.GetUtcNow(), Token);
        await store.AppendAsync(run, EventEntity.Task, "node/task", (int)ExecutionStatus.Ready, clock.GetUtcNow(), Token);
        clock.Advance(2);
        await dispatcher.DispatchAsync(run, new(run.Snapshot.RunId, "node", "task", 1, Sha), Command(run, 1, clock), Token);
        await using var api = await MetricsApiServer.Start(database.DataSource, Token, browser: true);
        var jwt = api.Jwt(group);
        var dispatched = Assert.Single((await Read(api, run.Snapshot.RunId, jwt)).Attempts);
        Assert.Equal(first.AgentId.ToString("D"), dispatched.AgentId);
        Assert.Null(dispatched.StartedAt);
        Assert.Null(dispatched.EndedAt);
        Assert.Equal(0, dispatched.DurationMs);
        clock.Advance(3);
        await store.AcceptAsync(first, new AgentMessage { TaskStarted = new()
            { Task = Command(run, 1, clock).Task, At = Timestamp.FromDateTimeOffset(clock.GetUtcNow()) } }, Token);
        var active = await Read(api, run.Snapshot.RunId, jwt);
        Assert.NotNull(Assert.Single(active.Attempts).StartedAt);
        Assert.Equal(0, active.Attempts[0].DurationMs);
        Assert.Equal(JsonSerializer.Serialize(active), JsonSerializer.Serialize(await Read(api, run.Snapshot.RunId, jwt)));
        clock.Advance(4);
        Assert.Single(await sweeper.ReconcileAsync(first, [], Token));
        var lostAt = clock.GetUtcNow();
        clock.Advance(6);
        var second = await registry.RegisterAsync(Hello(), Token);
        Assert.NotEqual(first.AgentId, second.AgentId);
        Assert.Equal(AgentLossDisposition.Replaced,
            await dispatcher.ReplaceLostAsync(run.Snapshot.RunId, "node/task", 1, Token));
        clock.Advance(2);
        await store.AcceptAsync(second, new AgentMessage { TaskStarted = new()
            { Task = Command(run, 2, clock).Task, At = Timestamp.FromDateTimeOffset(clock.GetUtcNow()) } }, Token);
        clock.Advance(3);
        await store.AcceptAsync(second, new AgentMessage { TaskCompleted = new()
            { Task = Command(run, 2, clock).Task, At = Timestamp.FromDateTimeOffset(clock.GetUtcNow()),
                Outcome = TaskOutcome.Succeeded } }, Token);
        run = await store.LoadAsync(run.Snapshot.RunId, Token);
        await store.AppendAsync(run, EventEntity.Node, "node", (int)ExecutionStatus.Succeeded, clock.GetUtcNow(), Token);
        await store.AppendAsync(run, EventEntity.Run, null, (int)RunStatus.Succeeded, clock.GetUtcNow(), Token);
        await Execute("UPDATE agents SET name='private-machine-secret' WHERE id=$1", first.AgentId);
        await Execute("UPDATE task_attempts SET error='private-error' WHERE run_id=$1", run.Snapshot.RunId);
        await Execute("UPDATE node_runs SET outputs='{\"private-output\":\"never-project\"}' WHERE run_id=$1",
            run.Snapshot.RunId);
        var response = await Read(api, run.Snapshot.RunId, jwt);
        Assert.Equal(new[] { 1, 2 }, response.Attempts.Select(a => a.Attempt));
        Assert.Equal(new[] { first.AgentId.ToString("D"), second.AgentId.ToString("D") },
            response.Attempts.Select(a => a.AgentId));
        Assert.Equal(lostAt, response.Attempts[0].EndedAt);
        Assert.Equal(5000, response.Attempts[0].QueueWaitMs);
        Assert.Equal(4000, response.Attempts[0].DurationMs);
        Assert.Equal("Failed", response.Attempts[0].TerminalStatus);
        Assert.Equal("AgentLost", response.Attempts[0].FailureCategory);
        Assert.Equal(8000, response.Attempts[1].QueueWaitMs);
        Assert.Equal(3000, response.Attempts[1].DurationMs);
        Assert.Equal("Succeeded", response.Attempts[1].TerminalStatus);
        Assert.Null(response.Attempts[1].FailureCategory);
        var metrics = RunMetricsResponse.From((await store.LoadAsync(run.Snapshot.RunId, Token)).Snapshot).Tasks["node/task"];
        Assert.Equal(metrics.Attempts.Sum(a => a.DurationMs), response.Attempts.Sum(a => a.DurationMs));
        using var plan = await api.Get($"/api/v1/workflows/{definition.Name}/versions/{version:D}", jwt, Token);
        Assert.Equal(HttpStatusCode.OK, plan.StatusCode);
        if (browser) await Browsers(api, run.Snapshot.RunId, jwt);
        await Execute("UPDATE task_attempts SET agent_id=NULL WHERE run_id=$1 AND attempt=1", run.Snapshot.RunId);
        var legacy = await Read(api, run.Snapshot.RunId, jwt);
        Assert.Null(legacy.Attempts[0].AgentId);
        Assert.Equal(second.AgentId.ToString("D"), legacy.Attempts[1].AgentId);
        Assert.Equal(response.Attempts[0] with { AgentId = null }, legacy.Attempts[0]);
    }

    private async Task Browsers(MetricsApiServer api, Guid run, string jwt)
    {
        var frontend = Environment.GetEnvironmentVariable("WF_FRONTEND_ROOT");
        Assert.True(frontend is not null && File.Exists(System.IO.Path.Combine(frontend, "scripts", "timeline-browser.ts")),
            "WF_FRONTEND_ROOT must identify the repository frontend directory.");
        using (var snapshot = await api.Get($"/api/v1/runs/{run:D}", jwt, Token))
            Assert.Equal(HttpStatusCode.OK, snapshot.StatusCode);
        var start = new ProcessStartInfo("bun")
        {
            WorkingDirectory = frontend, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(System.IO.Path.Combine("scripts", "timeline-browser.ts"));
        start.Environment["WF_TIMELINE_API"] = api.Api;
        start.Environment["WF_TIMELINE_TOKEN"] = jwt;
        start.Environment["WF_TIMELINE_RUN"] = run.ToString("D");
        start.Environment["WF_TIMELINE_ARTIFACTS"] = System.IO.Path.Combine(ArtifactRoot(), "browsers");
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(Token);
        var stderr = process.StandardError.ReadToEndAsync(Token);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
            deadline.CancelAfter(TimeSpan.FromMinutes(3));
            await process.WaitForExitAsync(deadline.Token);
            Assert.True(process.ExitCode == 0, $"Actual Timeline browsers failed.\n{await stdout}\n{await stderr}");
            output.WriteLine(await stdout);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
        }
    }

    [Fact]
    public async Task Timeline_actual_author_retry_and_lifecycle_manifest_order_survive_legacy_replay_and_deleted_workflow()
    {
        var calls = 0;
        var workflow = Workflow.Define("timeline-scenario", "1", w => w.Type("metrics")
            .SystemInit(_ => { }).PreSetup(_ => { })
            .Node("z-build", n => n.Task("restore", _ => { }).Task("compile", _ =>
            {
                if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("private-author-error");
            }, p => p.Retry(1, Backoff.Fixed(TimeSpan.FromMilliseconds(5))).NoAutoRetry()))
            .Node("a-deploy", n => n.DependsOn("z-build").Task("deploy", _ => { }))
            .PostProcess(_ => { }).SystemPostProcess(_ => { }));
        var result = await new LocalEngine(workflow, output: TextWriter.Null).RunAsync(Token)
            .WaitAsync(TimeSpan.FromSeconds(20), Token);
        Assert.Equal(RunStatus.Succeeded, result.Snapshot.Status);
        Assert.Equal(2, calls);
        var (team, group) = await Registry();
        var version = Guid.CreateVersion7();
        var workflowId = $"{team}/metrics/timeline";
        await Execute("INSERT INTO workflows(id,type_id) VALUES($1,$2)", workflowId, team + "/metrics");
        var nodes = workflow.Nodes.Select(n => new { key = n.Key, tasks = n.Tasks.Select(t => new { key = t.Key }) });
        await Execute("""
            INSERT INTO workflow_versions(id,workflow_id,semver,manifest,package_uri,package_sha256,package_size,
                source_uri,source_sha256,source_size,git_commit,git_author,registered_by,sdk_version)
            VALUES($1,$2,'1.0.0',$3::jsonb,'packages/'||repeat('00',32)||'.zip',decode(repeat('00',32),'hex'),1,
                'sources/'||repeat('00',32),decode(repeat('00',32),'hex'),1,repeat('a',40),'timeline','timeline','1')
            """, version, workflowId, JsonSerializer.Serialize(new { nodes, variables = new[] { new
                { key = "private-default", @default = "private-default-value" } } }));
        var run = new RunState(result.Snapshot.RunId, workflow.Nodes, workflow.Metadata, Assert.Fail,
            result.Events[0].RunQueuedAt!.Value);
        var store = Store();
        await store.CreateAsync(run, workflowId, version, DateTimeOffset.UtcNow.AddHours(1), Token);
        foreach (var change in result.Events) await store.AppendAsync(run, change, Token);
        await Execute("UPDATE workflows SET deleted_at=now() WHERE id=$1", workflowId);
        await using var api = await MetricsApiServer.Start(database.DataSource, Token);
        var response = await Read(api, run.Snapshot.RunId, api.Jwt(group));
        var taskOrder = workflow.Nodes.SelectMany(n => n.Tasks.Select(t => n.Key + "/" + t.Key)).ToArray();
        Assert.Equal(taskOrder.SelectMany(k => result.Snapshot.Metrics!.Tasks[k].Attempts.Select(a => (k, a.Attempt))),
            response.Attempts.Select(a => (a.TaskKey, a.Attempt)));
        Assert.Equal(4, response.Attempts.Count(a => a.NodeKey.StartsWith('$')));
        Assert.Equal(2, response.Attempts.Count(a => a.TaskKey == "z-build/compile"));
        Assert.All(response.Attempts, a => Assert.Null(a.AgentId));
        Assert.Equal("TaskError", response.Attempts.First(a => a.TaskKey == "z-build/compile").FailureCategory);
        var expected = RunTimelineResponse.From((await store.LoadAsync(run.Snapshot.RunId, Token)).Snapshot,
            new Dictionary<(string, int), string?>(), taskOrder);
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(response));
        await Execute("""
            UPDATE run_snapshots SET payload=convert_to((convert_from(payload,'UTF8')::jsonb-'Metrics')::text,'UTF8')
            WHERE run_id=$1
            """, run.Snapshot.RunId);
        Assert.Equal(JsonSerializer.Serialize(response),
            JsonSerializer.Serialize(await Read(api, run.Snapshot.RunId, api.Jwt(group))));
    }

    [Theory]
    [InlineData(RegistryRole.Viewer, false)]
    [InlineData(RegistryRole.Operator, false)]
    [InlineData(RegistryRole.Author, false)]
    [InlineData(RegistryRole.Admin, false)]
    [InlineData(RegistryRole.Viewer, true)]
    [InlineData(RegistryRole.Operator, true)]
    [InlineData(RegistryRole.Author, true)]
    [InlineData(RegistryRole.Admin, true)]
    public async Task Timeline_fresh_Viewer_policy_all_roles_ownership_changes_and_revocation(RegistryRole role, bool type)
    {
        var (team, group) = await Registry(role, type);
        var run = await Create(team);
        await using var api = await MetricsApiServer.Start(database.DataSource, Token);
        var jwt = api.Jwt(group);
        Assert.Empty((await Read(api, run.Snapshot.RunId, jwt)).Attempts);
        using (var machine = await api.Get(Path(run.Snapshot.RunId), api.Jwt(group, identity: "app"), Token))
            await Problem(machine, 403, "insufficient_scope_role");
        var (foreignTeam, _) = await Registry();
        await Execute("UPDATE runs SET workflow_id=$2 WHERE id=$1", run.Snapshot.RunId, $"{foreignTeam}/metrics/foreign");
        using (var moved = await api.Get(Path(run.Snapshot.RunId), jwt, Token))
            await Problem(moved, 403, "insufficient_scope_role");
        await Execute("UPDATE runs SET workflow_id=$2 WHERE id=$1", run.Snapshot.RunId, $"{team}/metrics/workflow");
        Assert.Empty((await Read(api, run.Snapshot.RunId, jwt)).Attempts);
        await Execute("DELETE FROM role_bindings WHERE principal_id=$1", group);
        using var revoked = await api.Get(Path(run.Snapshot.RunId), jwt, Token);
        await Problem(revoked, 403, "insufficient_scope_role");
    }

    [Fact]
    public async Task Timeline_rejects_unknown_foreign_machine_anonymous_noncanonical_and_all_query_parameters()
    {
        var (team, group) = await Registry();
        var run = await Create(team);
        var (foreignTeam, _) = await Registry();
        var foreign = await Create(foreignTeam);
        await using var api = await MetricsApiServer.Start(database.DataSource, Token);
        var jwt = api.Jwt(group);
        foreach (var credential in new string?[] { null, "not-a-jwt" })
        {
            using var anonymous = await api.Get(Path(run.Snapshot.RunId), credential, Token);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            Assert.True(anonymous.Headers.CacheControl?.NoStore);
        }
        foreach (var id in new[] { Guid.CreateVersion7(), foreign.Snapshot.RunId })
        {
            using var denied = await api.Get(Path(id), jwt, Token);
            await Problem(denied, 403, "insufficient_scope_role");
            Assert.DoesNotContain(id.ToString(), await denied.Content.ReadAsStringAsync(Token));
        }
        foreach (var id in new[] { "invalid", Guid.Empty.ToString(), Guid.NewGuid().ToString(),
            run.Snapshot.RunId.ToString("N"), run.Snapshot.RunId.ToString().ToUpperInvariant() })
        {
            using var bad = await api.Get($"/api/v1/runs/{id}/timeline", jwt, Token);
            await Problem(bad, 400, "invalid_run_identity");
        }
        foreach (var suffix in new[] { "?team=other", "?limit=1", "?id=a&id=b", "?afterSeq=0", "?%61=1", "?x=" })
        {
            using var bad = await api.Get(Path(run.Snapshot.RunId) + suffix, jwt, Token);
            await Problem(bad, 400, "invalid_run_query");
        }
        var escapedPath = Path(run.Snapshot.RunId).Replace(run.Snapshot.RunId.ToString()[0].ToString(),
            "%" + ((int)run.Snapshot.RunId.ToString()[0]).ToString("X2"), StringComparison.Ordinal);
        using var escaped = await api.Get(escapedPath, jwt, Token);
        await Problem(escaped, 400, "invalid_run_identity");
    }

    [Fact]
    public async Task Timeline_pending_skipped_reused_and_legacy_unstarted_work_has_no_fabricated_rows()
    {
        var (team, group) = await Registry();
        var pending = New();
        await new CommandInbox(database.DataSource, TimeProvider.System).TriggerAsync(new(pending.Snapshot,
            $"{team}/metrics/pending", Guid.CreateVersion7(), DateTimeOffset.UtcNow.AddHours(1)), "test-user", Token);
        await using var api = await MetricsApiServer.Start(database.DataSource, Token);
        var response = await Read(api, pending.Snapshot.RunId, api.Jwt(group));
        Assert.Equal("Queued", response.Status);
        Assert.Equal(0, response.LastSeq);
        Assert.Empty(response.Attempts);
        await Execute("""
            UPDATE commands SET payload=jsonb_set(payload,'{InitialState,Metrics,Run,DurationMs}','3') WHERE run_id=$1
            """, pending.Snapshot.RunId);
        using (var corrupt = await api.Get(Path(pending.Snapshot.RunId), api.Jwt(group), Token))
            await Problem(corrupt, 500, "run_data_inconsistent");
        await Execute("UPDATE commands SET payload=payload#-'{InitialState,Metrics}' WHERE run_id=$1", pending.Snapshot.RunId);
        Assert.Empty((await Read(api, pending.Snapshot.RunId, api.Jwt(group))).Attempts);
        foreach (var status in new[] { ExecutionStatus.Skipped, ExecutionStatus.Reused, ExecutionStatus.Cancelled })
        {
            var run = await Create(team);
            await Store().AppendAsync(run, EventEntity.Task, "node/task", (int)status, DateTimeOffset.UtcNow, Token);
            Assert.Empty((await Read(api, run.Snapshot.RunId, api.Jwt(group))).Attempts);
        }
        var legacy = await Create(team);
        foreach (var status in new[] { ExecutionStatus.Ready, ExecutionStatus.Dispatched })
            await Store().AppendAsync(legacy, EventEntity.Task, "node/task", (int)status, DateTimeOffset.UtcNow, Token);
        var unstarted = Assert.Single((await Read(api, legacy.Snapshot.RunId, api.Jwt(group))).Attempts);
        Assert.Null(unstarted.AgentId);
        Assert.Null(unstarted.StartedAt);
        Assert.Null(unstarted.EndedAt);
        Assert.Null(unstarted.TerminalStatus);
        Assert.Equal(0, unstarted.DurationMs);
    }

    [Theory]
    [InlineData("missing-snapshot")]
    [InlineData("header-sequence")]
    [InlineData("missing-task")]
    [InlineData("missing-field")]
    [InlineData("negative-duration")]
    public async Task Timeline_corruption_reuses_committed_metrics_checks_and_sanitizes_500(string corruption)
    {
        var (team, group) = await Registry();
        var run = await Create(team);
        var expression = corruption switch
        {
            "missing-task" => "convert_from(payload,'UTF8')::jsonb #- '{Metrics,Tasks,node/task}'",
            "missing-field" => "convert_from(payload,'UTF8')::jsonb #- '{Metrics,Run,DurationMs}'",
            _ => "jsonb_set(convert_from(payload,'UTF8')::jsonb,'{Metrics,Run,DurationMs}','-1')"
        };
        await Execute(corruption switch
        {
            "missing-snapshot" => "DELETE FROM run_snapshots WHERE run_id=$1",
            "header-sequence" => "UPDATE runs SET last_seq=last_seq+1 WHERE id=$1",
            _ => $"UPDATE run_snapshots SET payload=convert_to(({expression})::text,'UTF8') WHERE run_id=$1"
        }, run.Snapshot.RunId);
        await using var api = await MetricsApiServer.Start(database.DataSource, Token);
        using var response = await api.Get(Path(run.Snapshot.RunId), api.Jwt(group), Token);
        await Problem(response, 500, "run_data_inconsistent");
    }

    [Fact]
    public async Task Timeline_actual_storage_failure_is_sanitized_503_not_unknown_agent_success()
    {
        var (team, group) = await Registry();
        var run = await Create(team);
        await using var api = await MetricsApiServer.Start(database.DataSource, Token);
        await Execute("ALTER TABLE task_attempts RENAME TO unavailable_timeline_attempts");
        try
        {
            using var response = await api.Get(Path(run.Snapshot.RunId), api.Jwt(group), Token);
            await Problem(response, 503, "run_data_unavailable");
        }
        finally { await Execute("ALTER TABLE unavailable_timeline_attempts RENAME TO task_attempts"); }
    }

    [Fact]
    public async Task Timeline_actual_OpenAPI_is_guarded_and_describes_exact_required_array_and_nullable_agent()
    {
        await using var api = await MetricsApiServer.Start(database.DataSource, Token);
        using (var anonymous = await api.Get("/openapi/v1.json", null, Token))
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using (var viewer = await api.Get("/openapi/v1.json", api.Jwt(Guid.NewGuid()), Token))
            Assert.Equal(HttpStatusCode.OK, viewer.StatusCode);
        using var exported = await api.Get("/openapi/v1.json", api.Jwt(role: AuthExtensions.Admin), Token);
        Assert.Equal(HttpStatusCode.OK, exported.StatusCode);
        using var document = JsonDocument.Parse(await exported.Content.ReadAsStringAsync(Token));
        AssertTimelineSchema(document.RootElement);
    }

    internal static void AssertTimelineSchema(JsonElement document)
    {
        var operation = document.GetProperty("paths").GetProperty("/api/v1/runs/{id}/timeline").GetProperty("get");
        Assert.Equal(new[] { "200", "400", "401", "403", "500", "503" },
            operation.GetProperty("responses").EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal("#/components/schemas/RunTimelineResponse", operation.GetProperty("responses").GetProperty("200")
            .GetProperty("content").GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
        Assert.Single(operation.GetProperty("parameters").EnumerateArray());
        var schemas = document.GetProperty("components").GetProperty("schemas");
        var response = schemas.GetProperty("RunTimelineResponse");
        Assert.Equal(new[] { "attempts", "lastSeq", "runId", "status" },
            response.GetProperty("required").EnumerateArray().Select(p => p.GetString()).Order());
        Assert.Equal("array", response.GetProperty("properties").GetProperty("attempts").GetProperty("type").GetString());
        var attempt = schemas.GetProperty("RunTimelineAttempt");
        Assert.Equal(12, attempt.GetProperty("properties").EnumerateObject().Count());
        Assert.Contains(attempt.GetProperty("properties").GetProperty("agentId").GetProperty("type").EnumerateArray(),
            option => option.GetString() == "null");
    }

    private async Task<RunTimelineResponse> Read(MetricsApiServer api, Guid id, string jwt)
    {
        using var response = await api.Get(Path(id), jwt, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var text = await response.Content.ReadAsStringAsync(Token);
        foreach (var secret in new[] { "private-variable", "private-metadata", "private-default", "private-author-error",
            "private-error", "private-output", "private-machine-secret", "variables", "outputs", "payload", "packageUri",
            "capabilities", "grants", "dispatch_payload", "execution_policy" })
            Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
        return JsonSerializer.Deserialize(text, RunTimelineJsonContext.Default.RunTimelineResponse)!;
    }

    private async Task Problem(HttpResponseMessage response, int status, string reason)
    {
        Assert.Equal(status, (int)response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var text = await response.Content.ReadAsStringAsync(Token);
        using var document = JsonDocument.Parse(text);
        Assert.Equal(reason, document.RootElement.GetProperty("reason").GetString());
        foreach (var secret in new[] { "SELECT", "Exception", "Npgsql", "task_attempts", "run_snapshots", "private-" })
            Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
    }

    private static RunState New() => new(Guid.CreateVersion7(), [new("node", [], [new("task")])],
        new("metrics", ImmutableSortedDictionary<string, string>.Empty.Add("secret", "private-metadata")), Assert.Fail,
        new Dictionary<string, JsonElement> { ["value"] = JsonSerializer.SerializeToElement("private-variable") });

    private async Task<RunState> Create(string team, Guid? version = null)
    {
        var run = New();
        await Store().CreateAsync(run, $"{team}/metrics/workflow", version ?? Guid.CreateVersion7(), DateTimeOffset.UtcNow.AddHours(1), Token);
        return run;
    }

    private async Task Version(WorkflowDefinition definition, Guid version)
    {
        await Execute("INSERT INTO workflows(id,type_id) VALUES($1,$2)", definition.Name,
            definition.Name[..definition.Name.LastIndexOf('/')]);
        await Execute("""
            INSERT INTO workflow_versions(id,workflow_id,semver,manifest,package_uri,package_sha256,package_size,
                source_uri,source_sha256,source_size,git_commit,git_author,registered_by,sdk_version)
            VALUES($1,$2,'1.0.0',$3::jsonb,'packages/'||repeat('00',32)||'.zip',decode(repeat('00',32),'hex'),1,
                'sources/'||repeat('00',32),decode(repeat('00',32),'hex'),1,repeat('a',40),'timeline','timeline','1')
            """, version, definition.Name, ManifestWriter.Write(definition));
    }

    private async Task<(string Team, Guid Group)> Registry(RegistryRole role = RegistryRole.Viewer, bool type = false)
    {
        var team = "timeline-" + Guid.NewGuid().ToString("N");
        var group = Guid.NewGuid();
        await using var connection = await database.DataSource.OpenConnectionAsync(Token);
        await using var transaction = await connection.BeginTransactionAsync(Token);
        await new RegistryWriter().UpsertAsync(connection, transaction,
            new(team, [new(team, "metrics", "Timeline integration", new([]),
                RoleBindings: type ? [new(role, group)] : [])],
                RoleBindings: type ? [] : [new(role, group)]), Token);
        await transaction.CommitAsync(Token);
        return (team, group);
    }

    private async Task Execute(string sql, params object[] values)
    {
        await using var command = database.DataSource.CreateCommand(sql);
        foreach (var value in values) command.Parameters.Add(new NpgsqlParameter { Value = value });
        await command.ExecuteNonQueryAsync(Token);
    }

    private static DispatchCommand Command(RunState run, uint attempt, TimeProvider clock) => new()
    {
        Task = new() { RunId = run.Snapshot.RunId.ToString("D"), NodeKey = "node", TaskKey = "task", Attempt = attempt },
        Version = "1", PackageUri = "https://packages.invalid/private-package.zip", Sha256 = Sha,
        DeadlineAt = Timestamp.FromDateTimeOffset(clock.GetUtcNow().AddMinutes(5))
    };

    private static Hello Hello()
    {
        var id = Guid.NewGuid();
        return new() { Id = id.ToString("D"), Name = $"timeline-{id:N}", Os = "linux", Arch = "x64",
            Cores = 2, RamMb = 8192, MaxTasks = 10, Pool = "ci", Version = "1", ProtocolVersion = 1 };
    }

    private static string Path(Guid id) => $"/api/v1/runs/{id:D}/timeline";
    internal static string ArtifactRoot()
    {
        var root = System.IO.Path.Combine(AppContext.BaseDirectory, "timeline-evidence");
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class TimelineClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(int seconds) => now += TimeSpan.FromSeconds(seconds);
    }
}

public sealed class RunTimelineDatabase : AgentDatabase
{
    public string ConnectionString => DatabaseConnectionString;
    protected override IReadOnlyList<string> Migrations => Migrator.LoadScripts().Select(script => script.Name).ToArray();
}
