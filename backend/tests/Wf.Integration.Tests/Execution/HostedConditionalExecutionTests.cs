using System.Text.Json;
using Wf.Contracts;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Definition;
using Wf.Sdk;

namespace Wf.Integration.Tests.Execution;

public sealed partial class HostedRunExecutionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cached_download_is_durably_skipped_and_series_load_receives_committed_outputs(bool cached)
    {
        await using var runtime = await Runtime.Create(database, w => w
            .Node("dataset", n => n.Task("check", _ => { })
                .Task("download", _ => { }, when: Condition.Output("dataset", "missing"))
                .Task("load", _ => { }))
            .Node("train", n => n.DependsOn("dataset").Input("dataset", "data").Task("fit", _ => { })));
        await runtime.Tick();
        await runtime.Complete(Assert.Single(await runtime.Pending()), outputs: cached ? """{"missing":false}""" : """{"missing":true}""");
        await runtime.FreshTick();
        var command = Assert.Single(await runtime.Pending());
        if (!cached)
        {
            Assert.Equal("download", command.Task.TaskKey);
            await runtime.Complete(command);
            await runtime.FreshTick();
            command = Assert.Single(await runtime.Pending());
        }
        Assert.Equal("load", command.Task.TaskKey);
        using (var outputs = JsonDocument.Parse(command.NodeOutputsJson.Memory))
            Assert.Equal(!cached, outputs.RootElement.GetProperty("missing").GetBoolean());
        await runtime.Complete(command, outputs: """{"data":42}""");
        await runtime.Tick();
        command = Assert.Single(await runtime.Pending());
        Assert.Equal("train", command.Task.NodeKey);
        using (var inputs = JsonDocument.Parse(command.InputsJson.Memory))
            Assert.Equal(42, inputs.RootElement.GetProperty("dataset").GetProperty("data").GetInt32());
        await runtime.Complete(command);
        await runtime.Finish();
        var snapshot = (await runtime.Load()).Snapshot;
        Assert.Equal(RunStatus.Succeeded, snapshot.Status);
        Assert.Equal(cached ? ExecutionStatus.Skipped : ExecutionStatus.Succeeded, snapshot.Tasks["dataset/download"]);
        Assert.Equal(cached ? ConditionOutcome.NotMatched : ConditionOutcome.Matched, snapshot.ConditionOutcomes["dataset/download"]);
        Assert.Equal(cached ? 3L : 4L, await runtime.Scalar("SELECT count(*) FROM task_attempts WHERE run_id=$1"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Node_condition_is_recovered_and_false_never_places_or_dispatches_its_tasks(bool enabled)
    {
        await using var runtime = await Runtime.Create(database, w => w
            .Variables(v => v.Bool("enabled", enabled))
            .Node("optional", n => n.When(Condition.Variable("enabled")).Task("task", _ => { }))
            .Node("success", n => n.DependsOn("optional").Task("task", _ => { }))
            .Node("always", n => n.DependsOn("optional", DependencyCondition.Always).Task("task", _ => { })));
        await runtime.FreshTick();
        var snapshot = (await runtime.Load()).Snapshot;
        Assert.Equal(enabled ? ConditionOutcome.Matched : ConditionOutcome.NotMatched, snapshot.ConditionOutcomes["optional"]);
        if (!enabled)
        {
            Assert.Equal("always", Assert.Single(await runtime.Pending()).Task.NodeKey);
            Assert.Equal(ExecutionStatus.Skipped, snapshot.Tasks["optional/task"]);
            Assert.Equal(ExecutionStatus.Skipped, snapshot.Nodes["success"]);
            Assert.Equal(0L, await runtime.Scalar("SELECT count(*) FROM task_attempts WHERE run_id=$1 AND task_key='optional/task'"));
        }
        await runtime.FreshTick();
        await runtime.Finish();
        Assert.Equal(RunStatus.Succeeded, (await runtime.Load()).Snapshot.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Bad_condition_is_a_durable_validation_failure_without_an_attempt(bool nodeCondition)
    {
        await using var runtime = await Runtime.Create(database, w => w
            .Node("source", n => n.Task("write", _ => { }))
            .Node("consumer", n =>
            {
                n.DependsOn("source").Input("source", "flag");
                if (nodeCondition) n.When(Condition.Output("source", "flag"));
                n.Task("never", _ => { }, p => p.Retry(3), when: nodeCondition ? null : Condition.Output("source", "flag"));
            }));
        await runtime.Tick();
        await runtime.Complete(Assert.Single(await runtime.Pending()), outputs: """{"flag":"true"}""");
        await runtime.FreshTick();
        await runtime.Finish();
        var snapshot = (await runtime.Load()).Snapshot;
        Assert.Equal(RunStatus.Failed, snapshot.Status);
        Assert.Equal(ConditionOutcome.InvalidType, snapshot.ConditionOutcomes[nodeCondition ? "consumer" : "consumer/never"]);
        Assert.Equal(ExecutionStatus.Failed, snapshot.Nodes["consumer"]);
        Assert.Equal(0L, await runtime.Scalar("SELECT count(*) FROM task_attempts WHERE run_id=$1 AND task_key='consumer/never'"));
        Assert.Single(snapshot.ConditionOutcomes);
    }

    [Fact]
    public async Task A_committed_true_task_condition_is_not_reevaluated_after_recovery_or_retry()
    {
        await using var runtime = await Runtime.Create(database, w => w
            .Node("source", n => n.Task("write", _ => { }))
            .Node("work", n => n.DependsOn("source").Input("source", "flag")
                .Task("retry", _ => { }, p => p.Retry(1, Backoff.Fixed(TimeSpan.FromSeconds(1))),
                    when: Condition.Output("source", "flag"))));
        await runtime.Tick();
        await runtime.Complete(Assert.Single(await runtime.Pending()), outputs: """{"flag":true}""");
        await runtime.Tick();
        var first = Assert.Single(await runtime.Pending());
        await runtime.Complete(first, TaskOutcome.Failed, "TaskError");
        await runtime.Execute("""UPDATE node_runs SET outputs='{"flag":false}'::jsonb WHERE run_id=$1 AND node_key='source'""", runtime.RunId);
        runtime.Clock.Now = runtime.Clock.Now.AddSeconds(2);
        await runtime.FreshTick();
        var retry = Assert.Single(await runtime.Pending());
        Assert.Equal(2u, retry.Task.Attempt);
        Assert.Equal(ConditionOutcome.Matched, (await runtime.Load()).Snapshot.ConditionOutcomes["work/retry"]);
        await runtime.Complete(retry);
        await runtime.Finish();
        Assert.Equal(RunStatus.Succeeded, (await runtime.Load()).Snapshot.Status);
    }

    [Fact]
    public async Task All_tasks_skipped_completes_a_running_node_without_creating_attempts()
    {
        await using var runtime = await Runtime.Create(database, w => w
            .Variables(v => v.Bool("enabled", false))
            .Node("all", n => n.Parallel(1)
                .Task("one", _ => { }, when: Condition.Variable("enabled"))
                .Task("two", _ => { }, when: Condition.Variable("enabled"))));
        await runtime.Finish();
        var snapshot = (await runtime.Load()).Snapshot;
        Assert.Equal(RunStatus.Succeeded, snapshot.Status);
        Assert.Equal(ExecutionStatus.Skipped, snapshot.Nodes["all"]);
        Assert.Equal(0L, await runtime.Scalar("SELECT count(*) FROM task_attempts WHERE run_id=$1"));
    }

    [Fact]
    public async Task Lifecycle_conditions_are_durable_without_dispatching_false_stages_or_tasks()
    {
        await using var runtime = await Runtime.Create(database, w => w
            .Variables(v => v.Bool("enabled", false))
            .PreSetup(s => s.When(Condition.Variable("enabled")).Task("never", _ => { }))
            .Node("body", n => n.Task("run", _ => { }))
            .PostProcess(s => s.Task("never", _ => { }, when: Condition.Variable("enabled"))));
        await runtime.Finish();
        var snapshot = (await runtime.Load()).Snapshot;
        Assert.Equal(RunStatus.Succeeded, snapshot.Status);
        Assert.Equal(ConditionOutcome.NotMatched, snapshot.ConditionOutcomes["$pre"]);
        Assert.Equal(ConditionOutcome.NotMatched, snapshot.ConditionOutcomes["$post/never"]);
        Assert.Equal(1L, await runtime.Scalar("SELECT count(*) FROM task_attempts WHERE run_id=$1"));
    }
}
