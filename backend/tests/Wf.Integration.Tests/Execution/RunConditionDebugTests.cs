using System.Net;
using System.Text.Json;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Contracts.Events;
using Wf.ControlPlane.Engine.Recovery;
using Wf.Engine.Core;
using Wf.Sdk;

namespace Wf.Integration.Tests.Execution;

public sealed partial class RunTimelineApiTests
{
    [Theory]
    [InlineData(ConditionOutcome.NotMatched, false)]
    [InlineData(ConditionOutcome.Matched, true)]
    public async Task Conditions_show_pinned_expression_recorded_value_and_variable_origin_without_other_values(
        ConditionOutcome outcome, bool actual)
    {
        var (team, group) = await Registry();
        var definition = Workflow.Define($"{team}/metrics/workflow", "1.0.0", w => w.Type("metrics")
            .Variables(v => v.Bool("runFinalTest", true).String("private", "private-default"))
            .Node("node", n => n.When(Condition.Variable("runFinalTest")).Task("task", _ => { })));
        var version = Guid.CreateVersion7();
        await Version(definition, version);
        var run = new RunState(Guid.CreateVersion7(), definition.Nodes, definition.Metadata, Assert.Fail,
            new Dictionary<string, JsonElement>
            {
                ["runFinalTest"] = JsonSerializer.SerializeToElement(actual),
                ["private"] = JsonSerializer.SerializeToElement("private-variable")
            });
        await Store().CreateAsync(run, definition.Name, version, DateTimeOffset.UtcNow.AddHours(1), Token);
        await Store().AppendAsync(run, EventEntity.Run, null, (int)RunStatus.Running, DateTimeOffset.UtcNow, Token);
        var decision = run.CreateEvent(EventEntity.Node, "node",
            (int)(actual ? ExecutionStatus.Ready : ExecutionStatus.Skipped), DateTimeOffset.UtcNow) with
        { ConditionOutcome = outcome };
        await Store().AppendAsync(run, decision, Token);
        await using var api = await MetricsApiServer.Start(database.DataSource, Token);
        using var response = await api.Get($"/api/v1/runs/{run.Snapshot.RunId}/conditions", api.Jwt(group), Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var text = await response.Content.ReadAsStringAsync(Token);
        Assert.DoesNotContain("private-", text);
        using var document = JsonDocument.Parse(text);
        var condition = Assert.Single(document.RootElement.GetProperty("conditions").EnumerateArray());
        Assert.Equal("node", condition.GetProperty("entityKey").GetString());
        Assert.Equal("runFinalTest", condition.GetProperty("variableKey").GetString());
        Assert.True(condition.GetProperty("expected").GetBoolean());
        Assert.Equal(actual, condition.GetProperty("actual").GetBoolean());
        Assert.Equal(outcome.ToString(), condition.GetProperty("outcome").GetString());
        Assert.Equal(decision.Sequence.ToString(), condition.GetProperty("evaluatedSequence").GetString());
        Assert.Equal(decision.At, condition.GetProperty("evaluatedAt").GetDateTimeOffset());
        Assert.Equal("RunVariable", condition.GetProperty("sourceKind").GetString());
        Assert.True(condition.GetProperty("declaredDefault").GetBoolean());
        Assert.Equal(version, document.RootElement.GetProperty("versionId").GetGuid());
    }

    [Fact]
    public async Task Conditions_pending_trigger_is_not_a_decision_and_validates_scope_and_query()
    {
        var (team, group) = await Registry();
        var definition = Workflow.Define($"{team}/metrics/workflow", "1.0.0", w => w.Type("metrics")
            .Variables(v => v.Bool("enabled", true))
            .Node("node", n => n.Task("task", _ => { }, when: Condition.Variable("enabled"))));
        var version = Guid.CreateVersion7();
        await Version(definition, version);
        var run = new RunState(Guid.CreateVersion7(), definition.Nodes, definition.Metadata, Assert.Fail);
        await new CommandInbox(database.DataSource, TimeProvider.System).TriggerAsync(new(run.Snapshot,
            definition.Name, version, DateTimeOffset.UtcNow.AddHours(1)), "test-user", Token);
        await using var api = await MetricsApiServer.Start(database.DataSource, Token);
        var path = $"/api/v1/runs/{run.Snapshot.RunId}/conditions";
        using (var response = await api.Get(path, api.Jwt(group), Token))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
            var item = Assert.Single(document.RootElement.GetProperty("conditions").EnumerateArray());
            Assert.Equal("node/task", item.GetProperty("entityKey").GetString());
            Assert.Equal(JsonValueKind.Null, item.GetProperty("actual").ValueKind);
            Assert.Equal(JsonValueKind.Null, item.GetProperty("outcome").ValueKind);
            Assert.Equal(JsonValueKind.Null, item.GetProperty("evaluatedAt").ValueKind);
        }
        using (var response = await api.Get(path, null, Token))
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using (var response = await api.Get(path, api.Jwt(Guid.NewGuid()), Token))
            await Problem(response, 403, "insufficient_scope_role");
        using (var response = await api.Get(path + "?includeSecrets=true", api.Jwt(group), Token))
            await Problem(response, 400, "invalid_run_query");
        using (var response = await api.Get(path.Replace(run.Snapshot.RunId.ToString(), "invalid"), api.Jwt(group), Token))
            await Problem(response, 400, "invalid_run_identity");
    }

    [Theory]
    [InlineData(ConditionOutcome.NotMatched)]
    [InlineData(ConditionOutcome.MissingValue)]
    [InlineData(ConditionOutcome.InvalidType)]
    public async Task Conditions_output_source_is_exact_successful_commit_before_decision_and_never_later_values(
        ConditionOutcome outcome)
    {
        var (team, group) = await Registry();
        var definition = Workflow.Define($"{team}/metrics/workflow", "1.0.0", w => w.Type("metrics")
            .Node("node", n => n.Series().Task("seed", _ => { })
                .Task("task", _ => { }, when: Condition.Output("node", "cacheMissing"))
                .Task("after", _ => { })));
        var version = Guid.CreateVersion7();
        await Version(definition, version);
        var run = new RunState(Guid.CreateVersion7(), definition.Nodes, definition.Metadata, Assert.Fail);
        await Store().CreateAsync(run, definition.Name, version, DateTimeOffset.UtcNow.AddHours(1), Token);
        await Store().AppendAsync(run, EventEntity.Run, null, (int)RunStatus.Running, DateTimeOffset.UtcNow, Token);
        foreach (var state in new[] { ExecutionStatus.Ready, ExecutionStatus.Dispatched, ExecutionStatus.Running })
            await Store().AppendAsync(run, EventEntity.Node, "node", (int)state, DateTimeOffset.UtcNow, Token);
        await ConditionOutput(run, "node/seed", outcome switch
        {
            ConditionOutcome.MissingValue => """{"unrelated":"private-output"}""",
            ConditionOutcome.InvalidType => """{"cacheMissing":"private-invalid-output"}""",
            _ => """{"cacheMissing":false,"unrelated":"private-output"}"""
        });
        if (outcome is ConditionOutcome.MissingValue or ConditionOutcome.InvalidType)
            await Store().AppendAsync(run, EventEntity.Task, "node/task", (int)ExecutionStatus.Ready, DateTimeOffset.UtcNow, Token);
        var decision = run.CreateEvent(EventEntity.Task, "node/task", (int)(outcome == ConditionOutcome.NotMatched
            ? ExecutionStatus.Skipped : ExecutionStatus.Failed), DateTimeOffset.UtcNow) with
        {
            ConditionOutcome = outcome,
            FailureCategory = outcome == ConditionOutcome.NotMatched ? null : FailureCategory.ValidationError
        };
        await Store().AppendAsync(run, decision, Token);
        await ConditionOutput(run, "node/after", """{"cacheMissing":true,"unrelated":"private-later-output"}""");
        await using var api = await MetricsApiServer.Start(database.DataSource, Token);
        using var response = await api.Get($"/api/v1/runs/{run.Snapshot.RunId}/conditions", api.Jwt(group), Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(Token);
        Assert.DoesNotContain("private-", text);
        using var document = JsonDocument.Parse(text);
        var item = Assert.Single(document.RootElement.GetProperty("conditions").EnumerateArray());
        Assert.Equal("node/task", item.GetProperty("entityKey").GetString());
        Assert.Equal("node", item.GetProperty("nodeKey").GetString());
        Assert.Equal("cacheMissing", item.GetProperty("outputKey").GetString());
        Assert.Equal("NodeOutput", item.GetProperty("sourceKind").GetString());
        Assert.Equal(outcome.ToString(), item.GetProperty("outcome").GetString());
        Assert.Equal(decision.Sequence.ToString(), item.GetProperty("evaluatedSequence").GetString());
        if (outcome == ConditionOutcome.NotMatched)
        {
            Assert.False(item.GetProperty("actual").GetBoolean());
            Assert.Equal("node/seed", item.GetProperty("producerTaskKey").GetString());
            Assert.Equal(1, item.GetProperty("producerAttempt").GetInt32());
            Assert.True(item.GetProperty("producedAt").GetDateTimeOffset() <= decision.At);
        }
        else
        {
            Assert.Equal(JsonValueKind.Null, item.GetProperty("actual").ValueKind);
            if (outcome == ConditionOutcome.InvalidType)
            {
                Assert.Equal("node/seed", item.GetProperty("producerTaskKey").GetString());
                Assert.Equal(1, item.GetProperty("producerAttempt").GetInt32());
            }
            else Assert.Equal(JsonValueKind.Null, item.GetProperty("producerTaskKey").ValueKind);
        }
    }

    [Fact]
    public async Task Conditions_legacy_decision_does_not_invent_a_timestamp_or_output_producer()
    {
        var (team, group) = await Registry();
        var definition = Workflow.Define($"{team}/metrics/workflow", "1.0.0", w => w.Type("metrics")
            .Variables(v => v.Bool("enabled", true))
            .Node("node", n => n.When(Condition.Variable("enabled")).Task("task", _ => { })));
        var version = Guid.CreateVersion7();
        await Version(definition, version);
        var run = new RunState(Guid.CreateVersion7(), definition.Nodes, definition.Metadata, Assert.Fail);
        await Store().CreateAsync(run, definition.Name, version, DateTimeOffset.UtcNow.AddHours(1), Token);
        await Store().AppendAsync(run, EventEntity.Run, null, (int)RunStatus.Running, DateTimeOffset.UtcNow, Token);
        await Store().AppendAsync(run, run.CreateEvent(EventEntity.Node, "node", (int)ExecutionStatus.Skipped,
            DateTimeOffset.UtcNow) with { ConditionOutcome = ConditionOutcome.NotMatched }, Token);
        await Execute("DELETE FROM run_events WHERE run_id=$1", run.Snapshot.RunId);
        await using var api = await MetricsApiServer.Start(database.DataSource, Token);
        using var response = await api.Get($"/api/v1/runs/{run.Snapshot.RunId}/conditions", api.Jwt(group), Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        var item = Assert.Single(document.RootElement.GetProperty("conditions").EnumerateArray());
        Assert.False(item.GetProperty("actual").GetBoolean());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("evaluatedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("evaluatedSequence").ValueKind);
    }

    [Fact]
    public async Task Conditions_reused_output_source_retains_original_attempt_without_using_the_childs_later_values()
    {
        var (team, group) = await Registry();
        var definition = Workflow.Define($"{team}/metrics/workflow", "1.0.0", w => w.Type("metrics")
            .Node("source", n => n.Task("seed", _ => { }))
            .Node("node", n => n.Input("source", "flag").DependsOn("source")
                .When(Condition.Output("source", "flag")).Task("task", _ => { })));
        var version = Guid.CreateVersion7();
        await Version(definition, version);
        async Task<RunState> NewRun()
        {
            var state = new RunState(Guid.CreateVersion7(), definition.Nodes, definition.Metadata, Assert.Fail);
            await Store().CreateAsync(state, definition.Name, version, DateTimeOffset.UtcNow.AddHours(1), Token);
            await Store().AppendAsync(state, EventEntity.Run, null, (int)RunStatus.Running, DateTimeOffset.UtcNow, Token);
            return state;
        }
        var original = await NewRun();
        foreach (var status in new[] { ExecutionStatus.Ready, ExecutionStatus.Dispatched, ExecutionStatus.Running })
            await Store().AppendAsync(original, EventEntity.Node, "source", (int)status, DateTimeOffset.UtcNow, Token);
        await ConditionOutput(original, "source/seed", """{"flag":false,"secret":"private-output"}""");
        await Store().AppendAsync(original, EventEntity.Node, "source", (int)ExecutionStatus.Succeeded, DateTimeOffset.UtcNow, Token);
        var child = await NewRun();
        await Store().AppendAsync(child, EventEntity.Node, "source", (int)ExecutionStatus.Reused, DateTimeOffset.UtcNow, Token);
        await Execute("""
            UPDATE node_runs SET reused_from_run_id=$2 WHERE run_id=$1 AND node_key='source'
            """, child.Snapshot.RunId, original.Snapshot.RunId);
        await Store().AppendAsync(child, child.CreateEvent(EventEntity.Node, "node", (int)ExecutionStatus.Skipped,
            DateTimeOffset.UtcNow) with { ConditionOutcome = ConditionOutcome.NotMatched }, Token);
        await using var api = await MetricsApiServer.Start(database.DataSource, Token);
        using var response = await api.Get($"/api/v1/runs/{child.Snapshot.RunId}/conditions", api.Jwt(group), Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        var item = Assert.Single(document.RootElement.GetProperty("conditions").EnumerateArray());
        Assert.Equal(original.Snapshot.RunId, item.GetProperty("sourceRunId").GetGuid());
        Assert.Equal("source/seed", item.GetProperty("producerTaskKey").GetString());
        Assert.Equal(1, item.GetProperty("producerAttempt").GetInt32());
        Assert.False(item.GetProperty("actual").GetBoolean());
    }

    [Fact]
    public async Task Conditions_missing_pinned_definition_is_an_explicit_sanitized_failure()
    {
        var (team, group) = await Registry();
        var run = await Create(team);
        await using var api = await MetricsApiServer.Start(database.DataSource, Token);
        using var response = await api.Get($"/api/v1/runs/{run.Snapshot.RunId}/conditions", api.Jwt(group), Token);
        await Problem(response, 500, "run_data_inconsistent");
    }

    private async Task ConditionOutput(RunState run, string key, string outputs)
    {
        await Store().AppendAsync(run, EventEntity.Task, key, (int)ExecutionStatus.Ready, DateTimeOffset.UtcNow, Token);
        foreach (var state in new[] { ExecutionStatus.Dispatched, ExecutionStatus.Running, ExecutionStatus.Succeeded })
            await Store().AppendAsync(run, run.CreateEvent(EventEntity.Task, key, (int)state, DateTimeOffset.UtcNow) with
            {
                Attempt = 1,
                AgentId = state == ExecutionStatus.Dispatched ? Guid.NewGuid() : null,
                AttemptStatus = state == ExecutionStatus.Succeeded ? ExecutionStatus.Succeeded : null
            }, Token);
        await Execute("""
            INSERT INTO task_attempts(run_day,run_id,task_key,attempt,status,deadline_at,outputs)
            VALUES(uuid_v7_run_day($1),$1,$2,1,$3,now()+interval '5 minutes',$4::json)
            """, run.Snapshot.RunId, key, (short)ExecutionStatus.Succeeded, outputs);
    }
}
