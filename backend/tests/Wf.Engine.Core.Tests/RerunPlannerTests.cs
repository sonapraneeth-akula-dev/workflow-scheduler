using System.Collections.Immutable;
using System.Text.Json;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Contracts.Events;
using Wf.Engine.Core.Rerun;
using Wf.Engine.Core.Scheduling;

namespace Wf.Engine.Core.Tests;

public sealed class RerunPlannerTests
{
    private static readonly Guid OriginalId = Guid.Parse("b99683b1-754d-474b-887a-1edb8e2ed489");
    private static readonly Guid Version = Guid.Parse("c2bb0902-a611-4f9e-839f-e6b489d15cfa");
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;
    private static readonly ImmutableArray<NodeDefinition> Nodes =
    [
        new("build", [], [new("compile"), new("package")]),
        new("deploy-eu", [new("build")], [new("run")]),
        new("deploy-us", [new("build")], [new("run")]),
        new("smoke", [new("deploy-eu"), new("deploy-us")], [new("run")])
    ];

    [Fact]
    public void Failed_and_downstream_nodes_reset_while_succeeded_parallel_branch_and_outputs_are_reused()
    {
        var source = Source();
        var plan = RerunPlanner.Plan(Guid.CreateVersion7(), source, Nodes);
        Assert.Equal(OriginalId, plan.ParentRunId);
        Assert.Equal(Version, plan.VersionId);
        Assert.NotEqual(OriginalId, plan.Snapshot.RunId);
        Assert.Equal(RunStatus.Queued, plan.Snapshot.Status);
        Assert.Equal(0, plan.Snapshot.LastSequence);
        Assert.Equal(ExecutionStatus.Reused, plan.Snapshot.Nodes["build"]);
        Assert.Equal(ExecutionStatus.Reused, plan.Snapshot.Nodes["deploy-eu"]);
        Assert.Equal(ExecutionStatus.Pending, plan.Snapshot.Nodes["deploy-us"]);
        Assert.Equal(ExecutionStatus.Pending, plan.Snapshot.Nodes["smoke"]);
        Assert.Equal(["build", "deploy-eu"], plan.ReusedNodes.Keys.Order(StringComparer.Ordinal));
        Assert.All(plan.ReusedNodes.Values, node => Assert.Equal(OriginalId, node.ReusedFromRunId));
        Assert.Equal("build-output", plan.ReusedNodes["build"].Outputs.GetProperty("value").GetString());
        Assert.Equal(source.Snapshot.Variables["environment"].GetRawText(), plan.Snapshot.Variables["environment"].GetRawText());
        Assert.Same(source.Snapshot.Metadata, plan.Snapshot.Metadata);
        Assert.Equal(ExecutionStatus.Failed, source.Snapshot.Nodes["deploy-us"]);
        Assert.Equal(42, source.Snapshot.LastSequence);
        Assert.Equal(ExecutionStatus.Reused, plan.Snapshot.Tasks["build/compile"]);
        Assert.Equal(ExecutionStatus.Reused, plan.Snapshot.Tasks["build/package"]);
        Assert.Equal(ExecutionStatus.Pending, plan.Snapshot.Tasks["deploy-us/run"]);
        Assert.Null(plan.Snapshot.Abort);
        Assert.Null(plan.Snapshot.EndedAt);
        Assert.Empty(plan.Snapshot.AbortTargets);
    }

    [Fact]
    public void Explicit_successful_root_invalidates_its_entire_downstream_even_previously_succeeded_nodes()
    {
        var source = Source(allSucceeded: true);
        var plan = RerunPlanner.Plan(Guid.CreateVersion7(), source, Nodes, ["deploy-eu"]);
        Assert.Equal(ExecutionStatus.Reused, plan.Snapshot.Nodes["build"]);
        Assert.Equal(ExecutionStatus.Reused, plan.Snapshot.Nodes["deploy-us"]);
        Assert.Equal(ExecutionStatus.Pending, plan.Snapshot.Nodes["deploy-eu"]);
        Assert.Equal(ExecutionStatus.Pending, plan.Snapshot.Nodes["smoke"]);
        Assert.False(plan.ReusedNodes.ContainsKey("smoke"));
    }

    [Fact]
    public void Successful_run_without_selection_means_a_full_rerun_not_a_no_work_success()
    {
        var plan = RerunPlanner.Plan(Guid.CreateVersion7(), Source(allSucceeded: true), Nodes);
        Assert.All(plan.Snapshot.Nodes.Values, status => Assert.Equal(ExecutionStatus.Pending, status));
        Assert.Empty(plan.ReusedNodes);
    }

    [Theory]
    [InlineData(DependencyCondition.OnSuccess)]
    [InlineData(DependencyCondition.OnFailure)]
    [InlineData(DependencyCondition.Always)]
    public void Every_dependency_condition_invalidates_old_downstream_outputs(DependencyCondition condition)
    {
        ImmutableArray<NodeDefinition> nodes = [new("root", [], [new("run")]), new("child", [new("root", condition)], [new("run")])];
        var source = Source(nodes, new Dictionary<string, ExecutionStatus>
        {
            ["root"] = ExecutionStatus.Failed, ["child"] = ExecutionStatus.Succeeded
        });
        var plan = RerunPlanner.Plan(Guid.CreateVersion7(), source, nodes);
        Assert.Empty(plan.ReusedNodes);
        Assert.All(plan.Snapshot.Nodes.Values, status => Assert.Equal(ExecutionStatus.Pending, status));
    }

    [Fact]
    public void Only_succeeded_or_previously_reused_body_nodes_are_ever_reusable()
    {
        ImmutableArray<NodeDefinition> nodes = [new("node", [], [])];
        foreach (var state in Enum.GetValues<ExecutionStatus>())
        {
            var source = Source(nodes, new Dictionary<string, ExecutionStatus> { ["node"] = state });
            var plan = RerunPlanner.Plan(Guid.CreateVersion7(), source, nodes);
            Assert.Equal(state is ExecutionStatus.Succeeded or ExecutionStatus.Reused ? ExecutionStatus.Reused : ExecutionStatus.Pending,
                plan.Snapshot.Nodes["node"]);
        }
    }

    [Fact]
    public void Lifecycle_stages_run_again_but_unchanged_body_can_be_reused()
    {
        var nodes = Nodes.Add(new(LifecycleStages.SystemInit, [], [new("run")]))
            .Add(new(LifecycleStages.PostProcess, [], [new("run")]));
        var statuses = Source().Snapshot.Nodes.Add(LifecycleStages.SystemInit, ExecutionStatus.Succeeded)
            .Add(LifecycleStages.PostProcess, ExecutionStatus.Succeeded);
        var plan = RerunPlanner.Plan(Guid.CreateVersion7(), Source(nodes, statuses), nodes);
        Assert.Equal(ExecutionStatus.Pending, plan.Snapshot.Nodes[LifecycleStages.SystemInit]);
        Assert.Equal(ExecutionStatus.Pending, plan.Snapshot.Nodes[LifecycleStages.PostProcess]);
        Assert.Equal(ExecutionStatus.Reused, plan.Snapshot.Nodes["build"]);
        Assert.False(plan.ReusedNodes.ContainsKey(LifecycleStages.SystemInit));
    }

    [Theory]
    [InlineData(LifecycleStages.SystemInit)]
    [InlineData(LifecycleStages.PreSetup)]
    public void Selected_setup_stage_invalidates_implicit_body_dependents(string stage)
    {
        var nodes = Nodes.Add(new(stage, [], [new("run")]));
        var statuses = Source(allSucceeded: true).Snapshot.Nodes.Add(stage, ExecutionStatus.Succeeded);
        var plan = RerunPlanner.Plan(Guid.CreateVersion7(), Source(nodes, statuses), nodes, [stage]);
        Assert.Empty(plan.ReusedNodes);
        Assert.All(plan.Snapshot.Nodes.Values, status => Assert.Equal(ExecutionStatus.Pending, status));
    }

    [Fact]
    public void Aborted_run_has_new_identity_without_old_abort_or_completion_data()
    {
        var source = Source();
        source = source with { Snapshot = source.Snapshot with
        {
            Status = RunStatus.Aborted, Abort = new("operator", "Operator", Now, "stop", AbortSource.API),
            EndedAt = Now.AddSeconds(1), AbortTargets = ["deploy-us/run"]
        } };
        var plan = RerunPlanner.Plan(Guid.CreateVersion7(), source, Nodes);
        Assert.Equal(RunStatus.Queued, plan.Snapshot.Status);
        Assert.Null(plan.Snapshot.Abort);
        Assert.Null(plan.Snapshot.EndedAt);
        Assert.Empty(plan.Snapshot.AbortTargets);
    }

    [Fact]
    public void Json_variables_and_outputs_are_owned_clones_not_lifetime_bound_to_source_documents()
    {
        var source = Source();
        using (var variables = JsonDocument.Parse("""{"nested":[1,2]}"""))
        using (var outputs = JsonDocument.Parse("""{"value":"durable"}"""))
        {
            source = source with
            {
                Snapshot = source.Snapshot with { Variables = source.Snapshot.Variables.SetItem("nested", variables.RootElement) },
                Outputs = source.Outputs.ToImmutableDictionary().SetItem("build", outputs.RootElement)
            };
            var plan = RerunPlanner.Plan(Guid.CreateVersion7(), source, Nodes);
            variables.Dispose();
            outputs.Dispose();
            Assert.Equal(2, plan.Snapshot.Variables["nested"].GetProperty("nested")[1].GetInt32());
            Assert.Equal("durable", plan.ReusedNodes["build"].Outputs.GetProperty("value").GetString());
        }
    }

    [Fact]
    public void Planned_snapshot_integrates_with_real_core_readiness_and_terminal_transitions()
    {
        var plan = RerunPlanner.Plan(Guid.CreateVersion7(), Source(), Nodes);
        var run = RunState.Restore(plan.Snapshot, [], Assert.Fail);
        run.Append(EventEntity.Run, null, (int)RunStatus.Running, Now);
        Assert.Equal(["deploy-us"], ReadinessEvaluator.Evaluate(Nodes, run.Snapshot.Nodes).Select(change => change.Node));
        Complete("deploy-us");
        Assert.Equal(["smoke"], ReadinessEvaluator.Evaluate(Nodes, run.Snapshot.Nodes).Select(change => change.Node));
        Complete("smoke");
        run.Append(EventEntity.Run, null, (int)RunStatus.Succeeded, Now);
        Assert.Equal(RunStatus.Succeeded, run.Snapshot.Status);
        Assert.DoesNotContain(run.Events, item => item.Key is "build" or "build/compile" or "build/package" or "deploy-eu" or "deploy-eu/run");
        var replay = RunState.Restore(plan.Snapshot, run.Events, Assert.Fail);
        Assert.Equal(run.Snapshot.Nodes, replay.Snapshot.Nodes);
        Assert.Equal(run.Snapshot.Tasks, replay.Snapshot.Tasks);
        return;

        void Complete(string node)
        {
            foreach (var state in new[] { ExecutionStatus.Ready, ExecutionStatus.Dispatched, ExecutionStatus.Running })
                run.Append(EventEntity.Node, node, (int)state, Now);
            foreach (var state in new[] { ExecutionStatus.Ready, ExecutionStatus.Dispatched, ExecutionStatus.Running, ExecutionStatus.Succeeded })
                run.Append(EventEntity.Task, node + "/run", (int)state, Now);
            run.Append(EventEntity.Node, node, (int)ExecutionStatus.Succeeded, Now);
        }
    }

    [Theory]
    [InlineData(RunStatus.Queued)]
    [InlineData(RunStatus.Running)]
    [InlineData(RunStatus.Aborting)]
    [InlineData((RunStatus)99)]
    public void Nonterminal_or_unknown_source_run_is_rejected(RunStatus status)
    {
        var source = Source();
        Assert.Throws<InvalidOperationException>(() => RerunPlanner.Plan(Guid.CreateVersion7(),
            source with { Snapshot = source.Snapshot with { Status = status } }, Nodes));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("Build")]
    [InlineData("")]
    [InlineData(" ")]
    public void Selection_uses_exact_declared_node_keys(string key) =>
        Assert.Throws<ArgumentException>(() => RerunPlanner.Plan(Guid.CreateVersion7(), Source(), Nodes, [key]));

    [Fact]
    public void Empty_duplicate_or_null_selection_keys_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => RerunPlanner.Plan(Guid.CreateVersion7(), Source(), Nodes, []));
        Assert.Throws<ArgumentException>(() => RerunPlanner.Plan(Guid.CreateVersion7(), Source(), Nodes, ["build", "build"]));
        Assert.Throws<ArgumentException>(() => RerunPlanner.Plan(Guid.CreateVersion7(), Source(), Nodes, [null!]));
    }

    [Fact]
    public void Snapshot_definition_and_reusable_output_mismatches_fail_instead_of_dropping_data()
    {
        var source = Source();
        RerunSource[] invalid =
        [
            source with { VersionId = Guid.Empty },
            source with { Snapshot = source.Snapshot with { Nodes = source.Snapshot.Nodes.Remove("build") } },
            source with { Snapshot = source.Snapshot with { Tasks = source.Snapshot.Tasks.Remove("build/compile") } },
            source with { Snapshot = source.Snapshot with { Nodes = source.Snapshot.Nodes.SetItem("build", (ExecutionStatus)99) } },
            source with { Outputs = source.Outputs.ToImmutableDictionary().Remove("build") },
            source with { Outputs = source.Outputs.ToImmutableDictionary().SetItem("build", JsonSerializer.SerializeToElement("not-an-object")) },
            source with { Outputs = source.Outputs.ToImmutableDictionary().Add("foreign", JsonSerializer.SerializeToElement(new { })) }
        ];
        foreach (var item in invalid)
            Assert.Throws<ArgumentException>(() => RerunPlanner.Plan(Guid.CreateVersion7(), item, Nodes));
        Assert.Throws<ArgumentException>(() => RerunPlanner.Plan(Guid.Empty, source, Nodes));
        Assert.Throws<ArgumentException>(() => RerunPlanner.Plan(OriginalId, source, Nodes));
    }

    [Fact]
    public void Duplicate_cyclic_unknown_and_null_definition_entities_are_rejected()
    {
        var source = Source();
        Assert.Throws<ArgumentException>(() => RerunPlanner.Plan(Guid.CreateVersion7(), source, Nodes.Add(Nodes[0])));
        Assert.Throws<ArgumentException>(() => RerunPlanner.Plan(Guid.CreateVersion7(), source, Nodes.Add(null!)));
        ImmutableArray<NodeDefinition> unknown = [new("node", [new("missing")], [])];
        Assert.Throws<ArgumentException>(() => RerunPlanner.Plan(Guid.CreateVersion7(), Source(unknown,
            new Dictionary<string, ExecutionStatus> { ["node"] = ExecutionStatus.Failed }), unknown));
        ImmutableArray<NodeDefinition> cycle = [new("a", [new("b")], []), new("b", [new("a")], [])];
        Assert.Throws<ArgumentException>(() => RerunPlanner.Plan(Guid.CreateVersion7(), Source(cycle,
            new Dictionary<string, ExecutionStatus> { ["a"] = ExecutionStatus.Failed, ["b"] = ExecutionStatus.Failed }), cycle));
    }

    [Fact]
    public void Deep_graph_invalidation_is_iterative_and_has_no_recursion_limit()
    {
        var nodes = Enumerable.Range(0, 10_000).Select(index => new NodeDefinition($"n{index}",
            index == 0 ? [] : [new($"n{index - 1}")], [])).ToImmutableArray();
        var states = nodes.ToDictionary(node => node.Key, _ => ExecutionStatus.Succeeded, StringComparer.Ordinal);
        states["n0"] = ExecutionStatus.Failed;
        var source = Source(nodes, states) with { Outputs = ImmutableDictionary<string, JsonElement>.Empty };
        var plan = RerunPlanner.Plan(Guid.CreateVersion7(), source, nodes);
        Assert.Equal(10_000, plan.Snapshot.Nodes.Count);
        Assert.All(plan.Snapshot.Nodes.Values, state => Assert.Equal(ExecutionStatus.Pending, state));
        Assert.Empty(plan.ReusedNodes);
    }

    private static RerunSource Source(bool allSucceeded = false) => Source(Nodes, Nodes.ToDictionary(node => node.Key,
        node => allSucceeded ? ExecutionStatus.Succeeded : node.Key switch
        {
            "deploy-us" => ExecutionStatus.Failed, "smoke" => ExecutionStatus.Skipped, _ => ExecutionStatus.Succeeded
        }, StringComparer.Ordinal), allSucceeded ? RunStatus.Succeeded : RunStatus.Failed);

    private static RerunSource Source(ImmutableArray<NodeDefinition> nodes,
        IReadOnlyDictionary<string, ExecutionStatus> states, RunStatus status = RunStatus.Failed)
    {
        var metadata = new WorkflowMetadata("release", ImmutableSortedDictionary<string, string>.Empty);
        var snapshot = new RunSnapshot(OriginalId, status, 42, states.ToImmutableDictionary(StringComparer.Ordinal),
            nodes.SelectMany(node => node.Tasks.Select(task => KeyValuePair.Create($"{node.Key}/{task.Key}", states[node.Key])))
                .ToImmutableDictionary(StringComparer.Ordinal), metadata,
            ImmutableDictionary<string, JsonElement>.Empty.Add("environment", JsonSerializer.SerializeToElement("dev")));
        var outputs = nodes.ToImmutableDictionary(node => node.Key,
            node => JsonSerializer.SerializeToElement(new { value = node.Key + "-output" }), StringComparer.Ordinal);
        return new(Version, snapshot, outputs);
    }
}
