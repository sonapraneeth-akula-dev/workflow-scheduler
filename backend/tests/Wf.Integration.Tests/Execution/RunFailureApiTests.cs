using System.Net;
using System.Text.Json;
using Npgsql;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Contracts.Events;
using Wf.ControlPlane.Api;
using Wf.ControlPlane.Registry;
using Wf.Engine.Core;
using Wf.Sdk;
using Wf.Sdk.Local;

namespace Wf.Integration.Tests.Execution;

public sealed partial class RunTimelineApiTests
{
    [Fact]
    public async Task Failure_projection_distinguishes_undispatched_missing_messages_and_pending_retries()
    {
        var (team, group) = await Registry();
        var store = Store();
        var undispatched = await Create(team);
        await store.AppendAsync(undispatched, EventEntity.Run, null, (int)RunStatus.Running, DateTimeOffset.UtcNow, Token);
        foreach (var status in new[] { ExecutionStatus.Ready, ExecutionStatus.Dispatched, ExecutionStatus.Running, ExecutionStatus.Failed })
            await store.AppendAsync(undispatched, EventEntity.Node, "node", (int)status, DateTimeOffset.UtcNow, Token);
        await Execute("UPDATE node_runs SET failure_category=$2 WHERE run_id=$1",
            undispatched.Snapshot.RunId, (short)FailureCategory.Placement);
        await store.AppendAsync(undispatched, EventEntity.Run, null, (int)RunStatus.Failed, DateTimeOffset.UtcNow, Token);
        await using var api = await MetricsApiServer.Start(database.DataSource, Token);
        var timeline = await Read(api, undispatched.Snapshot.RunId, api.Jwt(group));
        Assert.Empty(timeline.Attempts);
        var missing = Assert.Single(timeline.Failures);
        Assert.Equal(("Infra", "Unclassified", "Failed"), (missing.Type, missing.Category, missing.Impact));
        Assert.False(missing.MessageRecorded);
        Assert.Null(missing.TaskKey);
        Assert.Null(missing.Attempt);
        Assert.Contains("No original failure message was recorded", missing.Message);

        var retrying = await Create(team);
        await store.AppendAsync(retrying, EventEntity.Run, null, (int)RunStatus.Running, DateTimeOffset.UtcNow, Token);
        await Execute("""
            INSERT INTO task_attempts(run_day,run_id,task_key,attempt,status,deadline_at,retry_at,failure_category,error)
            VALUES(uuid_v7_run_day($1),$1,'node/task',1,7,now()+interval '5 minutes',now()+interval '3 seconds',$2,
                'Synthetic infrastructure network failure: connection refused')
            """, retrying.Snapshot.RunId, (short)FailureCategory.TaskError);
        await using var connection = await database.DataSource.OpenConnectionAsync(Token);
        var catalog = FailureCatalog.Load(System.IO.Path.Combine(AppContext.BaseDirectory, "failure-categories"));
        var failures = await RunFailures.Read(connection, catalog, [retrying.Snapshot.RunId], Token);
        Assert.Equal("Retrying", Assert.Single(failures[retrying.Snapshot.RunId]).Impact);
        await Execute("UPDATE runs SET status=$2 WHERE id=$1", retrying.Snapshot.RunId, (short)RunStatus.Failed);
        failures = await RunFailures.Read(connection, catalog, [retrying.Snapshot.RunId], Token);
        Assert.Equal("Failed", Assert.Single(failures[retrying.Snapshot.RunId]).Impact);
    }

    [Fact]
    public async Task Failure_summaries_preserve_actual_service_infra_retry_optional_and_success_outcomes_across_authorized_views()
    {
        var group = Guid.NewGuid();
        await using (var connection = await database.DataSource.OpenConnectionAsync(Token))
        await using (var transaction = await connection.BeginTransactionAsync(Token))
        {
            await new RegistryWriter().UpsertAsync(connection, transaction,
                new("demo", [new("demo", "metrics", "Failure integration", new([]))],
                    RoleBindings: [new(Wf.Contracts.Registry.RegistryRole.Viewer, group)]), Token);
            await transaction.CommitAsync(Token);
        }
        await using var api = await MetricsApiServer.Start(database.DataSource, Token, browser: true);
        var jwt = api.Jwt(group);
        var runs = new Dictionary<string, Guid>();
        foreach (var scenario in new[] { "success", "retry", "service", "infra" })
        {
            var calls = 0;
            var definition = Workflow.Define($"demo/metrics/failure-{scenario}", "1", w => w.Type("metrics")
                .Defaults(p => p.NoAutoRetry())
                .Node("fetch", n => n.Task("request", _ =>
                {
                    if (scenario == "retry" && Interlocked.Increment(ref calls) < 3)
                        throw new TaskFailureException("Synthetic infrastructure network failure: DEMO connection refused",
                            retryable: true, category: FailureCategory.TaskError);
                }, p => p.NoAutoRetry().Retry(2, Backoff.Fixed(TimeSpan.FromMilliseconds(1)), [FailureCategory.TaskError])))
                .Node("optional-check", n => n.DependsOn("fetch").Task("inspect", _ =>
                {
                    if (scenario != "success")
                        throw new TaskFailureException("Synthetic optional diagnostic failure; required work continues.",
                            retryable: false, category: FailureCategory.TaskError);
                }), failure: Failure.Soft)
                .Node("validate", n => n.DependsOn("fetch").Task("total", _ =>
                {
                    if (scenario is "service" or "infra")
                        throw new TaskFailureException(scenario == "infra"
                            ? "Synthetic infrastructure storage failure: blob storage unavailable for DEMO validation"
                            : "Synthetic required validation failure: DEMO order rejected",
                            retryable: false, category: scenario == "infra" ? FailureCategory.PlatformError : FailureCategory.ValidationError);
                }))
                .Node("report", n => n.DependsOn("optional-check", DependencyCondition.Always)
                    .DependsOn("validate", DependencyCondition.Always).Task("publish", _ => { })));
            var result = await new LocalEngine(definition, output: TextWriter.Null).RunAsync(Token)
                .WaitAsync(TimeSpan.FromSeconds(20), Token);
            Assert.Equal(scenario is "service" or "infra" ? RunStatus.Failed : RunStatus.Succeeded, result.Snapshot.Status);
            Assert.Equal(ExecutionStatus.Succeeded, result.Snapshot.Nodes["report"]);
            var version = Guid.CreateVersion7();
            await Version(definition, version);
            await Execute("UPDATE workflows SET latest_version_id=$2 WHERE id=$1", definition.Name, version);
            var run = new RunState(result.Snapshot.RunId, definition.Nodes, definition.Metadata, Assert.Fail,
                result.Events[0].RunQueuedAt!.Value);
            var store = Store();
            await store.CreateAsync(run, definition.Name, version, DateTimeOffset.UtcNow.AddHours(1), Token);
            foreach (var change in result.Events) await store.AppendAsync(run, change, Token);
            // Local execution has no dispatcher: persist its actual attempts and errors for the API fixture.
            foreach (var attempt in result.Attempts)
            {
                var metric = result.Snapshot.Metrics!.Tasks[attempt.TaskKey].Attempts.Single(a => a.Attempt == attempt.Attempt);
                await Execute("""
                    INSERT INTO task_attempts(run_day,run_id,task_key,attempt,status,deadline_at,queued_at,
                        started_at,ended_at,failure_category,error)
                    VALUES(uuid_v7_run_day($1),$1,$2,$3,$4,$5,$6,$7,$8,$9,$10)
                    """, run.Snapshot.RunId, attempt.TaskKey, (short)attempt.Attempt, (short)attempt.Status,
                    attempt.EndedAt.AddMinutes(1), metric.QueuedAt, attempt.StartedAt, attempt.EndedAt,
                    (object?)(short?)attempt.Failure ?? DBNull.Value, (object?)attempt.Error?.Message ?? DBNull.Value);
            }
            var timeline = await Read(api, run.Snapshot.RunId, jwt);
            runs.Add(scenario, run.Snapshot.RunId);
            if (scenario == "success")
            {
                Assert.Empty(timeline.Failures);
                Assert.Null(timeline.FailureSummary);
            }
            else
            {
                var summary = Assert.IsType<RunFailureSummary>(timeline.FailureSummary);
                Assert.All(timeline.Failures, failure => Assert.True(failure.MessageRecorded));
                Assert.Contains(timeline.Failures, failure =>
                    failure.Type == "Service" && failure.Category == "Diagnostics" && failure.Impact == "Optional");
                if (scenario == "retry")
                {
                    Assert.Equal(2, summary.RecoveredCount);
                    Assert.Equal(3, calls);
                    Assert.Equal(2, timeline.Failures.Count(failure =>
                        failure.Type == "Infra" && failure.Category == "Network" && failure.Impact == "Recovered"));
                }
                else
                {
                    Assert.Equal((scenario == "infra" ? "Infra" : "Service",
                            scenario == "infra" ? "Storage" : "Validation", "Failed"),
                        (summary.Primary.Type, summary.Primary.Category, summary.Primary.Impact));
                }
            }
            foreach (var path in new[]
            {
                $"/api/v1/runs?workflowId={definition.Name}",
                $"/api/v1/workflows/{definition.Name}/history",
                $"/api/v1/runs/history?workflowId={definition.Name}",
                "/api/v1/workflows?team=demo&type=metrics",
                "/api/v1/service-history?team=demo&type=metrics"
            })
            {
                using var response = await api.Get(path, jwt, Token);
                var text = await response.Content.ReadAsStringAsync(Token);
                Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path}: {response.StatusCode} {text}");
                using var document = JsonDocument.Parse(text);
                var item = document.RootElement.GetProperty("items").EnumerateArray().Single(item =>
                    path.Contains("service-history", StringComparison.Ordinal)
                        ? item.GetProperty("workflow").GetProperty("id").GetString() == definition.Name
                        : path.StartsWith("/api/v1/workflows?", StringComparison.Ordinal)
                            ? item.GetProperty("id").GetString() == definition.Name
                            : item.GetProperty(path.StartsWith("/api/v1/runs?", StringComparison.Ordinal) ? "id" : "runId")
                                .GetGuid() == run.Snapshot.RunId);
                var actual = path.Contains("service-history", StringComparison.Ordinal)
                    ? item.GetProperty("runs")[0].GetProperty("failureSummary")
                    : path.StartsWith("/api/v1/workflows?", StringComparison.Ordinal)
                        ? item.GetProperty("latestRun").GetProperty("failureSummary")
                        : item.GetProperty("failureSummary");
                Assert.Equal(JsonSerializer.SerializeToElement(timeline.FailureSummary,
                    RunTimelineJsonContext.Default.RunFailureSummary).GetRawText(), actual.GetRawText());
                if (path.Contains("service-history", StringComparison.Ordinal))
                    Assert.Equal(actual.GetRawText(),
                        item.GetProperty("workflow").GetProperty("latestRun").GetProperty("failureSummary").GetRawText());
                using var denied = await api.Get(path, api.Jwt(), Token);
                Assert.DoesNotContain("Synthetic", await denied.Content.ReadAsStringAsync(Token));
            }
        }
        if (Environment.GetEnvironmentVariable("WF_FAILURE_BROWSER") == "true")
            await Browsers(api, runs["infra"], jwt, runs);
    }
}
