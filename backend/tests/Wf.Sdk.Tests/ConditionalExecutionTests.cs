using System.Text.Json;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Contracts.Events;
using Wf.Contracts.Manifest;
using Wf.Engine.Core;
using Wf.Sdk;
using Wf.Sdk.Local;
using Wf.Validation.Rules;

namespace Wf.Sdk.Tests;

public sealed class ConditionalExecutionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Series_task_condition_skips_only_that_task_and_preserves_outputs(bool cached)
    {
        var calls = new List<string>();
        var workflow = Workflow.Define("cache", "1", w => w.Type("test")
            .Node("dataset", n => n
                .Task("check", ctx => { ctx.Outputs["missing"] = !cached; calls.Add("check"); })
                .Task("download", _ => { calls.Add("download"); }, when: Condition.Output("dataset", "missing"))
                .Task("load", ctx => { ctx.Outputs["data"] = 42; calls.Add("load"); }))
            .Node("train", n => n.DependsOn("dataset").Input("dataset", "data")
                .Task("fit", ctx => { Assert.Equal(42, ctx.Input<int>("dataset", "data")); calls.Add("fit"); })));
        var result = await new LocalEngine(workflow, output: TextWriter.Null).RunAsync(Token);
        Assert.Equal(RunStatus.Succeeded, result.Snapshot.Status);
        Assert.Equal(cached ? ExecutionStatus.Skipped : ExecutionStatus.Succeeded, result.Snapshot.Tasks["dataset/download"]);
        Assert.Equal(cached ? ConditionOutcome.NotMatched : ConditionOutcome.Matched,
            result.Snapshot.ConditionOutcomes["dataset/download"]);
        Assert.Equal(cached ? ["check", "load", "fit"] : new[] { "check", "download", "load", "fit" }, calls);
        Assert.Equal(cached ? 3 : 4, result.Attempts.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Node_condition_preserves_success_and_always_dependency_semantics(bool enabled)
    {
        var workflow = Workflow.Define("optional", "1", w => w.Type("test")
            .Variables(v => v.Bool("enabled", enabled))
            .Node("optional", n => n.When(Condition.Variable("enabled")).Task("task", _ => { }))
            .Node("success", n => n.DependsOn("optional").Task("task", _ => { }))
            .Node("always", n => n.DependsOn("optional", DependencyCondition.Always).Task("task", _ => { })));
        var result = await new LocalEngine(workflow, output: TextWriter.Null).RunAsync(Token);
        Assert.Equal(RunStatus.Succeeded, result.Snapshot.Status);
        Assert.Equal(enabled ? ExecutionStatus.Succeeded : ExecutionStatus.Skipped, result.Snapshot.Nodes["optional"]);
        Assert.Equal(enabled ? ExecutionStatus.Succeeded : ExecutionStatus.Skipped, result.Snapshot.Nodes["success"]);
        Assert.Equal(ExecutionStatus.Succeeded, result.Snapshot.Nodes["always"]);
        Assert.Equal(enabled ? 3 : 1, result.Attempts.Length);
        var initial = new RunState(result.Snapshot.RunId, workflow.Nodes, workflow.Metadata, Assert.Fail,
            result.Events[0].At, result.Snapshot.Variables);
        var restored = RunState.Restore(initial.Snapshot, result.Events, Assert.Fail);
        Assert.Equal(result.Snapshot.ConditionOutcomes, restored.Snapshot.ConditionOutcomes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_or_non_boolean_output_fails_without_attempt_or_retry(bool invalidType)
    {
        var calls = 0;
        var workflow = Workflow.Define("invalid", "1", w => w.Type("test")
            .Node("source", n => n.Task("write", ctx => { if (invalidType) ctx.Outputs["flag"] = "true"; }))
            .Node("consumer", n => n.DependsOn("source").Input("source", "flag")
                .Task("conditional", _ => { calls++; }, p => p.Retry(3), when: Condition.Output("source", "flag"))
                .Task("never", _ => { calls++; })));
        var result = await new LocalEngine(workflow, output: TextWriter.Null).RunAsync(Token);
        Assert.Equal(RunStatus.Failed, result.Snapshot.Status);
        Assert.Equal(0, calls);
        Assert.Single(result.Attempts);
        Assert.Equal(ExecutionStatus.Failed, result.Snapshot.Tasks["consumer/conditional"]);
        Assert.Equal(ExecutionStatus.Cancelled, result.Snapshot.Tasks["consumer/never"]);
        Assert.Equal(invalidType ? ConditionOutcome.InvalidType : ConditionOutcome.MissingValue,
            result.Snapshot.ConditionOutcomes["consumer/conditional"]);
        Assert.Contains(result.Events, change => change.Key == "consumer/conditional" &&
            change.FailureCategory == FailureCategory.ValidationError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invalid_node_condition_is_explicit_and_respects_soft_policy(bool soft)
    {
        var workflow = Workflow.Define("invalid-node", "1", w => w.Type("test")
            .Variables(v => v.Bool("optional"))
            .Node("work", n => n.When(Condition.Variable("optional")).Task("never", _ => Assert.Fail()),
                failure: soft ? FailurePolicy.Soft : FailurePolicy.Hard));
        var result = await new LocalEngine(workflow, output: TextWriter.Null).RunAsync(Token);
        Assert.Equal(soft ? RunStatus.Succeeded : RunStatus.Failed, result.Snapshot.Status);
        Assert.Equal(soft ? ExecutionStatus.SoftFailed : ExecutionStatus.Failed, result.Snapshot.Nodes["work"]);
        Assert.Equal(ExecutionStatus.Skipped, result.Snapshot.Tasks["work/never"]);
        Assert.Equal(ConditionOutcome.MissingValue, result.Snapshot.ConditionOutcomes["work"]);
        Assert.Empty(result.Attempts);
    }

    [Fact]
    public async Task Parallel_skips_do_not_consume_slots_and_all_skipped_node_is_skipped()
    {
        var workflow = Workflow.Define("parallel", "1", w => w.Type("test")
            .Variables(v => v.Bool("disabled", false))
            .Node("work", n => n.Parallel(1)
                .Task("skip", _ => Assert.Fail(), when: Condition.Variable("disabled"))
                .Task("run", _ => { }))
            .Node("all", n => n.Parallel()
                .Task("skip", _ => Assert.Fail(), when: Condition.Variable("disabled"))));
        var result = await new LocalEngine(workflow, output: TextWriter.Null).RunAsync(Token);
        Assert.Equal(RunStatus.Succeeded, result.Snapshot.Status);
        Assert.Equal(ExecutionStatus.Succeeded, result.Snapshot.Nodes["work"]);
        Assert.Equal(ExecutionStatus.Skipped, result.Snapshot.Nodes["all"]);
        Assert.Equal("work/run", Assert.Single(result.Attempts).TaskKey);
    }

    [Fact]
    public async Task Failed_task_outputs_cannot_drive_a_later_condition()
    {
        var workflow = Workflow.Define("committed", "1", w => w.Type("test")
            .Node("work", n => n
                .Task("fail", ctx => { ctx.Outputs["flag"] = true; throw new NonRetryableException("expected"); },
                    failure: FailurePolicy.Soft)
                .Task("conditional", _ => Assert.Fail(), when: Condition.Output("work", "flag"))));
        var result = await new LocalEngine(workflow, output: TextWriter.Null).RunAsync(Token);
        Assert.Equal(ConditionOutcome.MissingValue, result.Snapshot.ConditionOutcomes["work/conditional"]);
        Assert.Empty(result.Outputs["work"]);
        Assert.Single(result.Attempts);
    }

    [Fact]
    public async Task Lifecycle_stage_and_task_conditions_use_the_same_pre_dispatch_path()
    {
        var workflow = Workflow.Define("stages", "1", w => w.Type("test")
            .Variables(v => v.Bool("enabled", false))
            .PreSetup(s => s.When(Condition.Variable("enabled")).Task("never", _ => Assert.Fail()))
            .Node("body", n => n.Task("run", _ => { }))
            .PostProcess(s => s.Task("never", _ => Assert.Fail(), when: Condition.Variable("enabled"))));
        var result = await new LocalEngine(workflow, output: TextWriter.Null).RunAsync(Token);
        Assert.Equal(RunStatus.Succeeded, result.Snapshot.Status);
        Assert.Equal(ConditionOutcome.NotMatched, result.Snapshot.ConditionOutcomes["$pre"]);
        Assert.Equal(ConditionOutcome.NotMatched, result.Snapshot.ConditionOutcomes["$post/never"]);
        Assert.Equal("body/run", Assert.Single(result.Attempts).TaskKey);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("text")]
    public void Authoring_rejects_unknown_or_non_boolean_variables(string variable)
    {
        Assert.Throws<ArgumentException>(() => Workflow.Define("invalid", "1", w => w.Type("test")
            .Variables(v => v.String("text", "true"))
            .Node("work", n => n.When(Condition.Variable(variable)).Task("never", _ => { }))));
    }

    [Fact]
    public void Authoring_rejects_unstable_or_undeclared_output_references()
    {
        Assert.Throws<ArgumentException>(() => Workflow.Define("invalid", "1", w => w.Type("test")
            .Node("work", n => n.When(Condition.Output("work", "flag")).Task("never", _ => { }))));
        Assert.Throws<ArgumentException>(() => Workflow.Define("invalid", "1", w => w.Type("test")
            .Node("work", n => n.Parallel().Task("first", _ => { })
                .Task("never", _ => { }, when: Condition.Output("work", "flag")))));
        Assert.Throws<ArgumentException>(() => Workflow.Define("invalid", "1", w => w.Type("test")
            .Node("source", n => n.Task("write", _ => { }))
            .Node("work", n => n.DependsOn("source").When(Condition.Output("source", "flag")).Task("never", _ => { }))));
    }

    [Fact]
    public void Conditions_round_trip_and_raw_manifest_validation_rejects_bad_reference()
    {
        var workflow = Workflow.Define("manifest", "1", w => w.Type("test")
            .Variables(v => v.Bool("enabled", true))
            .RunsOn(Machines.Pool("demo"))
            .Node("work", n => n.When(Condition.Variable("enabled"))
                .Task("task", _ => { }, when: Condition.Variable("enabled", expected: false))));
        var json = ManifestWriter.Write(workflow);
        var manifest = JsonSerializer.Deserialize(json, WorkflowManifestJsonContext.Default.WorkflowManifest)!;
        Assert.Equal(Condition.Variable("enabled"), manifest.Nodes[0].When);
        Assert.Equal(Condition.Variable("enabled", false), manifest.Nodes[0].Tasks[0].When);
        Assert.Empty(ManifestRules.Validate(manifest, cancellationToken: Token));
        using var invalid = JsonDocument.Parse(json.Replace("\"variableKey\": \"enabled\"", "\"variableKey\": \"unknown\"",
            StringComparison.Ordinal));
        Assert.All(ManifestRules.Validate(invalid.RootElement, cancellationToken: Token), error => Assert.Equal("WFV028", error.RuleId));
        Assert.Equal(2, ManifestRules.Validate(invalid.RootElement, cancellationToken: Token).Length);
    }
}
