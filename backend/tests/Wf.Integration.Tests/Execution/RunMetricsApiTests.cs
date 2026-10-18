using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Contracts.Events;
using Wf.Contracts.Registry;
using Wf.ControlPlane.Api;
using Wf.ControlPlane.Auth;
using Wf.ControlPlane.Data;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Recovery;
using Wf.ControlPlane.Registry;
using Wf.Engine.Core;
using Wf.Integration.Tests.Agents;
using Wf.Sdk;
using Wf.Sdk.Local;

namespace Wf.Integration.Tests.Execution;

public sealed class RunMetricsApiTests(RunMetricsApiDatabase database) : IClassFixture<RunMetricsApiDatabase>
{
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private RunStore Store() => new(database.DataSource, NullLogger<RunStore>.Instance);

    public static TheoryData<bool, FailureCategory> Categories
    {
        get
        {
            var data = new TheoryData<bool, FailureCategory>();
            foreach (var stage in new[] { false, true })
            foreach (var category in Enum.GetValues<FailureCategory>())
                data.Add(stage, category);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Categories))]
    public async Task Actual_author_failure_categories_reach_stage_node_run_and_attempt_through_durable_HTTP(
        bool stage, FailureCategory category)
    {
        var workflow = Workflow.Define("metrics-causes", "1", w =>
        {
            w.Type("metrics");
            if (stage) w.SystemInit((Action<TaskContext>)(_ => throw new TaskFailureException("private-scripted-stage-error",
                retryable: false, category: category)));
            w.Node("node", n => n.Task("task", _ =>
            {
                if (!stage) throw new TaskFailureException("private-scripted-node-error", retryable: false, category: category);
            }));
        });
        var result = await new LocalEngine(workflow, output: TextWriter.Null).RunAsync(Token)
            .WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Equal(RunStatus.Failed, result.Snapshot.Status);
        var (team, group) = await Registry();
        await Persist(workflow, result, team);
        await using var api = await MetricsApiServer.Start(database.DataSource, Token);
        var response = await Read(api, result.Snapshot.RunId, api.Jwt(group));
        var key = stage ? "$init" : "node";
        Assert.Equal(category.ToString(), response.Run.FailureCategory);
        Assert.Equal(category.ToString(), (stage ? response.Stages : response.Nodes)[key].FailureCategory);
        var task = response.Tasks[key + (stage ? "/run" : "/task")];
        Assert.Equal(category.ToString(), task.FailureCategory);
        Assert.Equal(category.ToString(), Assert.Single(task.Attempts).FailureCategory);
        Assert.Equal(JsonSerializer.Serialize(RunMetricsResponse.From(result.Snapshot)), JsonSerializer.Serialize(response));
    }

    [Theory]
    [InlineData("run-timeout")]
    [InlineData("node-timeout")]
    [InlineData("abort")]
    public async Task Actual_deadlines_and_abort_producer_causes_survive_committed_replay_and_HTTP(string cause)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runTimeout = cause == "run-timeout" ? TimeSpan.FromSeconds(1) : TimeSpan.FromMinutes(1);
        var nodeTimeout = cause == "node-timeout" ? TimeSpan.FromMilliseconds(100) : runTimeout;
        var workflow = Workflow.Define("metrics-cancellation", "1", w => w.Type("metrics")
            .RunTimeout(runTimeout)
            .Node("node", n => n.Timeout(nodeTimeout)
                .Task("task", async ctx =>
                {
                    started.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, ctx.CancellationToken);
                }, p => p.Timeout(nodeTimeout))));
        var engine = new LocalEngine(workflow, output: TextWriter.Null);
        var execution = engine.RunAsync(Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        if (cause == "abort")
            await engine.AbortAsync(new("test-user", "Test User", DateTimeOffset.UtcNow, "test abort", AbortSource.Local));
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(10), Token);
        var (team, group) = await Registry();
        await Persist(workflow, result, team);
        await using var api = await MetricsApiServer.Start(database.DataSource, Token);
        var response = await Read(api, result.Snapshot.RunId, api.Jwt(group));
        var expected = cause == "abort" ? "Aborted" : "Timeout";
        Assert.Equal(expected, response.Run.FailureCategory);
        Assert.Equal(expected, response.Nodes["node"].FailureCategory);
        Assert.Equal(expected, response.Tasks["node/task"].FailureCategory);
        Assert.Equal(expected, Assert.Single(response.Tasks["node/task"].Attempts).FailureCategory);
        Assert.Equal(cause == "run-timeout" ? "TimedOut" : cause == "abort" ? "Aborted" : "Failed", response.Status);
        Assert.Equal(JsonSerializer.Serialize(RunMetricsResponse.From(result.Snapshot)), JsonSerializer.Serialize(response));
    }

    [Fact]
    public async Task Scenario_A_actual_runtime_full_stages_author_and_http_retries_survive_Postgres_and_production_JWT_HTTP()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var http = builder.Build();
        var requests = 0;
        http.MapGet("/work", () => Interlocked.Increment(ref requests) == 1 ? Results.StatusCode(503) : Results.Ok());
        await http.StartAsync(Token);
        var authorCalls = 0;
        var stages = new List<string>();
        var workflow = Workflow.Define("scenario-a", "1", w => w.Type("metrics").Metadata("private", "metadata-secret")
            .SystemInit(_ => stages.Add("$init")).PreSetup(_ => stages.Add("$pre"))
            .Node("build", n => n.Task("restore", _ => { }).Task("compile", _ =>
            {
                if (Interlocked.Increment(ref authorCalls) == 1) throw new InvalidOperationException("private-author-error");
            }, p => p.Retry(1, Backoff.Fixed(TimeSpan.FromMilliseconds(5))).NoAutoRetry()))
            .Node("deploy-eu", n => n.DependsOn("build").Task("deploy", _ => { }))
            .Node("deploy-us", n => n.DependsOn("build").Task("deploy", _ => { }))
            .Node("smoke", n => n.DependsOn("deploy-eu", "deploy-us").Parallel(4)
                .Task("api", Tasks.Http(HttpMethod.Get, http.Urls.Single() + "/work"),
                    p => p.Retry(1, Backoff.Fixed(TimeSpan.FromMilliseconds(5))).NoAutoRetry())
                .Task("ui", _ => { })
                .Task("perf-probe", _ => { }, failure: Failure.Soft))
            .PostProcess(_ => stages.Add("$post")).SystemPostProcess(_ => stages.Add("$sys-post")));
        var result = await new LocalEngine(workflow, output: TextWriter.Null).RunAsync(Token)
            .WaitAsync(TimeSpan.FromSeconds(20), Token);
        Assert.Equal(RunStatus.Succeeded, result.Snapshot.Status);
        Assert.Equal(new[] { "$init", "$pre", "$post", "$sys-post" }, stages);
        Assert.Equal(2, authorCalls);
        Assert.Equal(2, requests);
        var (team, group) = await Registry();
        var run = new RunState(result.Snapshot.RunId, workflow.Nodes, workflow.Metadata, Assert.Fail,
            result.Events[0].RunQueuedAt!.Value);
        var store = Store();
        await store.CreateAsync(run, $"{team}/metrics/scenario-a", Guid.CreateVersion7(), DateTimeOffset.UtcNow.AddHours(1), Token);
        foreach (var change in result.Events) await store.AppendAsync(run, change, Token);
        await using var api = await MetricsApiServer.Start(database.DataSource, Token);
        var response = await Read(api, run.Snapshot.RunId, api.Jwt(group));
        Assert.Equal("Succeeded", response.Status);
        Assert.Equal(result.Events[^1].Sequence, response.LastSeq);
        Assert.Equal(4, response.Stages.Count);
        Assert.Equal(4, response.Nodes.Count);
        Assert.Equal(11, response.Tasks.Count);
        Assert.Equal(2, response.Run.RetryCount);
        Assert.Equal(1, response.Nodes["build"].RetryCount);
        Assert.Equal(1, response.Nodes["smoke"].RetryCount);
        Assert.Equal("TaskError", response.Tasks["build/compile"].Attempts[0].FailureCategory);
        Assert.Equal("TaskError", response.Tasks["smoke/api"].Attempts[0].FailureCategory);
        Assert.Null(response.Tasks["build/compile"].FailureCategory);
        Assert.All(response.Stages.Values.Concat(response.Nodes.Values), metric =>
        {
            Assert.Equal("Succeeded", metric.Status);
            Assert.NotNull(metric.StartedAt);
            Assert.NotNull(metric.EndedAt);
            Assert.Equal((metric.EndedAt.Value - metric.StartedAt.Value).TotalMilliseconds, metric.DurationMs);
        });
        Assert.All(response.Tasks.Values, task =>
        {
            Assert.Equal(task.Attempts.Sum(a => a.DurationMs), task.DurationMs);
            Assert.Equal(task.Attempts.Sum(a => a.QueueWaitMs), task.QueueWaitMs);
            Assert.Equal(task.Attempts.Count - 1, task.RetryCount);
            Assert.All(task.Attempts, attempt =>
            {
                Assert.Equal((attempt.EndedAt!.Value - attempt.StartedAt!.Value).TotalMilliseconds, attempt.DurationMs);
                Assert.Equal((attempt.StartedAt.Value - attempt.QueuedAt).TotalMilliseconds, attempt.QueueWaitMs);
            });
        });
        Assert.Equal(JsonSerializer.Serialize(RunMetricsResponse.From(result.Snapshot)), JsonSerializer.Serialize(response));
        Assert.Equal(JsonSerializer.Serialize(response),
            JsonSerializer.Serialize(await Read(api, run.Snapshot.RunId, api.Jwt(group))));
        await Execute("""
            UPDATE run_snapshots SET payload=convert_to((convert_from(payload,'UTF8')::jsonb-'Metrics')::text,'UTF8')
            WHERE run_id=$1
            """, run.Snapshot.RunId);
        Assert.Equal(JsonSerializer.Serialize(response),
            JsonSerializer.Serialize(await Read(api, run.Snapshot.RunId, api.Jwt(group))));
        await Execute("""
            UPDATE run_events SET payload=convert_to((convert_from(payload,'UTF8')::jsonb-'FailureCategory')::text,'UTF8')
            WHERE run_id=$1
            """, run.Snapshot.RunId);
        var legacy = await Read(api, run.Snapshot.RunId, api.Jwt(group));
        Assert.All(legacy.Tasks.Values.SelectMany(t => t.Attempts), a => Assert.Null(a.FailureCategory));
        Assert.Equal(response.Tasks["build/compile"].DurationMs, legacy.Tasks["build/compile"].DurationMs);
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
    public async Task Fresh_Run_Viewer_policy_honors_all_roles_at_team_or_type_and_immediate_revocation(RegistryRole role, bool type)
    {
        var (team, group) = await Registry(role, type);
        var run = await Create(team);
        await using var api = await MetricsApiServer.Start(database.DataSource, Token);
        var jwt = api.Jwt(group);
        Assert.Equal("Queued", (await Read(api, run.Snapshot.RunId, jwt)).Status);
        using var stranger = await api.Get(Path(run.Snapshot.RunId), api.Jwt(Guid.NewGuid()), Token);
        await Problem(stranger, 403, "insufficient_scope_role");
        using var machine = await api.Get(Path(run.Snapshot.RunId), api.Jwt(group, identity: "app"), Token);
        await Problem(machine, 403, "insufficient_scope_role");
        await Execute("DELETE FROM role_bindings WHERE principal_id=$1", group);
        using var revoked = await api.Get(Path(run.Snapshot.RunId), jwt, Token);
        await Problem(revoked, 403, "insufficient_scope_role");
    }

    [Fact]
    public async Task Unknown_foreign_unauthenticated_invalid_and_noncanonical_requests_never_leak_a_run_or_SPA()
    {
        var (team, group) = await Registry();
        var run = await Create(team);
        var (foreignTeam, _) = await Registry();
        var foreign = await Create(foreignTeam);
        await using var api = await MetricsApiServer.Start(database.DataSource, Token);
        var jwt = api.Jwt(group);
        using var anonymous = await api.Get(Path(run.Snapshot.RunId), null, Token);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var unauthenticated = await api.Get(Path(run.Snapshot.RunId), "not-a-jwt", Token);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        foreach (var id in new[] { Guid.CreateVersion7(), foreign.Snapshot.RunId })
        {
            using var denied = await api.Get(Path(id), jwt, Token);
            await Problem(denied, 403, "insufficient_scope_role");
            Assert.DoesNotContain(id.ToString(), await denied.Content.ReadAsStringAsync(Token));
        }
        foreach (var id in new[] { "invalid", Guid.Empty.ToString(), Guid.NewGuid().ToString(),
            run.Snapshot.RunId.ToString("N"), run.Snapshot.RunId.ToString().ToUpperInvariant() })
        {
            using var bad = await api.Get($"/api/v1/runs/{id}/metrics", jwt, Token);
            await Problem(bad, 400, "invalid_run_identity");
        }
        foreach (var suffix in new[] { "?team=other", "?limit=1", "?id=a&id=b", "?afterSeq=0", "?%61=1", "?x=" })
        {
            using var bad = await api.Get(Path(run.Snapshot.RunId) + suffix, jwt, Token);
            await Problem(bad, 400, "invalid_run_query");
        }
        using var escaped = await api.Get(Path(run.Snapshot.RunId).Replace(run.Snapshot.RunId.ToString()[0].ToString(),
            "%" + ((int)run.Snapshot.RunId.ToString()[0]).ToString("X2"), StringComparison.Ordinal), jwt, Token);
        await Problem(escaped, 400, "invalid_run_identity");
    }

    [Fact]
    public async Task Accepted_pending_trigger_and_active_skipped_reused_work_expose_only_recorded_facts()
    {
        var (team, group) = await Registry();
        var run = New();
        await new CommandInbox(database.DataSource, TimeProvider.System).TriggerAsync(new(run.Snapshot,
            $"{team}/metrics/pending", Guid.CreateVersion7(), DateTimeOffset.UtcNow.AddHours(1)), "test-user", Token);
        await using var api = await MetricsApiServer.Start(database.DataSource, Token);
        var pending = await Read(api, run.Snapshot.RunId, api.Jwt(group));
        Assert.Equal(0, pending.LastSeq);
        Assert.Equal("Queued", pending.Status);
        Assert.Null(pending.Run.StartedAt);
        Assert.Empty(pending.Tasks["node/task"].Attempts);
        await Execute("""
            UPDATE commands SET payload=jsonb_set(payload,'{InitialState,Metrics,Run,DurationMs}','3') WHERE run_id=$1
            """, run.Snapshot.RunId);
        using (var corrupt = await api.Get(Path(run.Snapshot.RunId), api.Jwt(group), Token))
            await Problem(corrupt, 500, "run_data_inconsistent");
        await Execute("UPDATE commands SET payload=payload#-'{InitialState,Metrics}' WHERE run_id=$1", run.Snapshot.RunId);
        Assert.Equal("Queued", (await Read(api, run.Snapshot.RunId, api.Jwt(group))).Status);
        var store = Store();
        var active = await Create(team);
        await store.AppendAsync(active, EventEntity.Run, null, (int)RunStatus.Running, DateTimeOffset.UtcNow, Token);
        foreach (var status in new[] { ExecutionStatus.Ready, ExecutionStatus.Dispatched, ExecutionStatus.Running })
            await store.AppendAsync(active, EventEntity.Task, "node/task", (int)status, DateTimeOffset.UtcNow, Token);
        var first = await Read(api, active.Snapshot.RunId, api.Jwt(group));
        await Task.Delay(20, Token);
        var second = await Read(api, active.Snapshot.RunId, api.Jwt(group));
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
        Assert.Equal("Running", first.Tasks["node/task"].Status);
        Assert.Equal(0, first.Tasks["node/task"].DurationMs);
        Assert.Null(Assert.Single(first.Tasks["node/task"].Attempts).EndedAt);
        foreach (var status in new[] { ExecutionStatus.Skipped, ExecutionStatus.Reused })
        {
            var neverStarted = await Create(team);
            await store.AppendAsync(neverStarted, EventEntity.Task, "node/task", (int)status, DateTimeOffset.UtcNow, Token);
            var metric = (await Read(api, neverStarted.Snapshot.RunId, api.Jwt(group))).Tasks["node/task"];
            Assert.Empty(metric.Attempts);
            Assert.Equal(0, metric.DurationMs);
            Assert.Null(metric.StartedAt);
            Assert.Null(metric.FailureCategory);
        }
    }

    [Theory]
    [InlineData("missing-snapshot")]
    [InlineData("missing-baseline")]
    [InlineData("missing-field")]
    [InlineData("missing-task")]
    [InlineData("negative-duration")]
    [InlineData("header-sequence")]
    [InlineData("invalid-category")]
    [InlineData("null-run")]
    public async Task Corrupt_or_missing_durable_data_returns_public_500_not_fabricated_metrics(string corruption)
    {
        var (team, group) = await Registry();
        var run = await Create(team);
        await Store().AppendAsync(run, EventEntity.Run, null, (int)RunStatus.Running, DateTimeOffset.UtcNow, Token);
        var id = run.Snapshot.RunId;
        var sql = corruption switch
        {
            "missing-snapshot" => "DELETE FROM run_snapshots WHERE run_id=$1",
            "missing-baseline" => """
                WITH removed AS (DELETE FROM run_snapshots WHERE run_id=$1 AND seq=0)
                UPDATE run_snapshots SET payload=convert_to((convert_from(payload,'UTF8')::jsonb-'Metrics')::text,'UTF8')
                WHERE run_id=$1 AND seq>0
                """,
            "header-sequence" => "UPDATE runs SET last_seq=last_seq+1 WHERE id=$1",
            _ => """
                UPDATE run_snapshots SET payload=convert_to((
                """ + (corruption switch
                {
                    "missing-field" => "convert_from(payload,'UTF8')::jsonb #- '{Metrics,Run,DurationMs}'",
                    "missing-task" => "convert_from(payload,'UTF8')::jsonb #- '{Metrics,Tasks,node/task}'",
                    "negative-duration" => "jsonb_set(convert_from(payload,'UTF8')::jsonb,'{Metrics,Run,DurationMs}','-1')",
                    "invalid-category" => "jsonb_set(convert_from(payload,'UTF8')::jsonb,'{Metrics,Run,FailureCategory}','999')",
                    "null-run" => "jsonb_set(convert_from(payload,'UTF8')::jsonb,'{Metrics,Run}','null')",
                    _ => throw new InvalidOperationException("Unrecognized corruption test.")
                }) + ")::text,'UTF8') WHERE run_id=$1"
        };
        await Execute(sql, id);
        await using var api = await MetricsApiServer.Start(database.DataSource, Token);
        using var response = await api.Get(Path(id), api.Jwt(group), Token);
        await Problem(response, 500, "run_data_inconsistent");
        var text = await response.Content.ReadAsStringAsync(Token);
        Assert.DoesNotContain("SELECT", text);
        Assert.DoesNotContain("Exception", text);
    }

    [Fact]
    public async Task Actual_OpenAPI_describes_typed_metrics_and_explicit_errors()
    {
        await using var api = await MetricsApiServer.Start(database.DataSource, Token);
        using var response = await api.Get("/openapi/v1.json", api.Jwt(role: AuthExtensions.Admin), Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        var operation = json.RootElement.GetProperty("paths").GetProperty("/api/v1/runs/{id}/metrics").GetProperty("get");
        Assert.Equal(new[] { "200", "400", "401", "403", "500", "503" },
            operation.GetProperty("responses").EnumerateObject().Select(p => p.Name).Order());
        Assert.Contains("RunMetricsResponse", operation.GetProperty("responses").GetProperty("200")
            .GetProperty("content").GetProperty("application/json").GetProperty("schema").GetRawText());
    }

    [Fact]
    public async Task Actual_database_failure_returns_public_503_and_never_storage_details()
    {
        var (team, group) = await Registry();
        var run = await Create(team);
        await using var api = await MetricsApiServer.Start(database.DataSource, Token);
        await Execute("ALTER TABLE run_snapshots RENAME TO unavailable_metrics_snapshots");
        try
        {
            using var response = await api.Get(Path(run.Snapshot.RunId), api.Jwt(group), Token);
            await Problem(response, 503, "run_data_unavailable");
            var text = await response.Content.ReadAsStringAsync(Token);
            Assert.DoesNotContain("run_snapshots", text);
            Assert.DoesNotContain("Npgsql", text);
        }
        finally { await Execute("ALTER TABLE unavailable_metrics_snapshots RENAME TO run_snapshots"); }
    }

    private static RunState New() => new(Guid.CreateVersion7(), [new("node", [], [new("task")])],
        new("metrics", ImmutableSortedDictionary<string, string>.Empty), Assert.Fail);

    private async Task<RunState> Create(string team)
    {
        var run = New();
        await Store().CreateAsync(run, $"{team}/metrics/workflow", Guid.CreateVersion7(), DateTimeOffset.UtcNow.AddHours(1), Token);
        return run;
    }

    private async Task Persist(WorkflowDefinition workflow, LocalRunResult result, string team)
    {
        var run = new RunState(result.Snapshot.RunId, workflow.Nodes, workflow.Metadata, Assert.Fail,
            result.Events[0].RunQueuedAt!.Value);
        var store = Store();
        await store.CreateAsync(run, $"{team}/metrics/workflow", Guid.CreateVersion7(), DateTimeOffset.UtcNow.AddHours(1), Token);
        foreach (var change in result.Events) await store.AppendAsync(run, change, Token);
    }

    private async Task<(string Team, Guid Group)> Registry(RegistryRole role = RegistryRole.Viewer, bool type = false)
    {
        var team = "metrics-" + Guid.NewGuid().ToString("N");
        var group = Guid.NewGuid();
        await using var connection = await database.DataSource.OpenConnectionAsync(Token);
        await using var transaction = await connection.BeginTransactionAsync(Token);
        await new RegistryWriter().UpsertAsync(connection, transaction,
            new(team, [new(team, "metrics", "Metrics integration", new([]),
                RoleBindings: type ? [new(role, group)] : [])],
                RoleBindings: type ? [] : [new(role, group)]), Token);
        await transaction.CommitAsync(Token);
        return (team, group);
    }

    private async Task<RunMetricsResponse> Read(MetricsApiServer api, Guid id, string jwt)
    {
        using var response = await api.Get(Path(id), jwt, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var text = await response.Content.ReadAsStringAsync(Token);
        foreach (var secret in new[] { "metadata-secret", "private-author-error", "variables", "packageUri", "outputs", "grants" })
            Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
        return JsonSerializer.Deserialize(text, MetricsJsonContext.Default.RunMetricsResponse)!;
    }

    private async Task Problem(HttpResponseMessage response, int status, string reason)
    {
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        Assert.Equal(status, json.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(reason, json.RootElement.GetProperty("reason").GetString());
    }

    private async Task Execute(string sql, params object[] values)
    {
        await using var command = database.DataSource.CreateCommand(sql);
        foreach (var value in values) command.Parameters.Add(new NpgsqlParameter { Value = value });
        await command.ExecuteNonQueryAsync(Token);
    }

    private static string Path(Guid id) => $"/api/v1/runs/{id:D}/metrics";
}

public sealed class RunMetricsApiDatabase : AgentDatabase
{
    protected override IReadOnlyList<string> Migrations => Migrator.LoadScripts().Select(script => script.Name).ToArray();
}
