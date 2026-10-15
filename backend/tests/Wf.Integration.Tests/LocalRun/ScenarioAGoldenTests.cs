using System.Text.Json;
using Wf.Contracts;
using Wf.Contracts.Events;
using Wf.Engine.Core;
using Wf.Engine.Core.Scheduling;
using Wf.Engine.Core.Stages;
using Wf.Engine.Core.Transitions;
using Wf.Sdk;
using Wf.Sdk.Local;

namespace Wf.Integration.Tests.LocalRun;

public sealed class ScenarioAGoldenTests
{
    [Fact]
    public async Task LocalRunMatchesCoreFixtureAndGoldenWithoutReorderingEvents()
    {
        var golden = JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "LocalRun", "scenario-a.events.json"), TestContext.Current.CancellationToken))!;
        for (var repetition = 0; repetition < 5; repetition++)
        {
            using var signals = new ExecutionSignals();
            var workflow = Define(signals);
            var core = ExecuteCoreFixture(workflow);
            var local = await new LocalEngine(workflow, output: signals).RunAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(golden, core.Events.Select(Format));
            Assert.Equal(golden, local.Events.Select(Format));
            Assert.Equal(core.Events.Select(e => e.Sequence), local.Events.Select(e => e.Sequence));
            Assert.Equal(RunStatus.Succeeded, local.Snapshot.Status);
            Assert.Equal(0, local.ExitCode);
        }
    }

    private static WorkflowDefinition Define(ExecutionSignals signals) => Workflow.Define("scenario-a", "1", w => w
        .Type("release-validation").Metadata("area", "checkout")
        .SystemInit(_ => { })
        .Node("build", n => n.Task("restore", _ => { }).Task("compile", _ => { }))
        .Node("deploy-eu", n => n.DependsOn("build").Task("deploy",
            ctx => signals.WaitAsync("Task deploy-us/deploy: Running", ctx.CancellationToken)))
        .Node("deploy-us", n => n.DependsOn("build").Task("deploy",
            ctx => signals.WaitAsync("Node deploy-eu: Succeeded", ctx.CancellationToken)))
        .Node("smoke", n => n.DependsOn("deploy-eu", "deploy-us").Parallel(4)
            .Task("api", ctx => signals.WaitAsync("Task smoke/perf-probe: Running", ctx.CancellationToken))
            .Task("ui", ctx => signals.WaitAsync("Task smoke/api: Succeeded", ctx.CancellationToken))
            .Task("perf-probe", ctx => signals.WaitAsync("Task smoke/ui: Succeeded", ctx.CancellationToken), failure: Failure.Soft))
        .PostProcess(_ => { }));

    private static RunState ExecuteCoreFixture(WorkflowDefinition workflow)
    {
        var run = new RunState(Guid.CreateVersion7(), workflow.Nodes, workflow.Metadata, Assert.Fail);
        Emit(EventEntity.Run, null, (int)RunStatus.Running);
        Ready();
        StartNode("$init");
        StartTask("$init/run");
        FinishTask("$init/run");
        FinishNode("$init");
        Ready();
        StartNode("build");
        StartTask("build/restore");
        FinishTask("build/restore");
        StartTask("build/compile");
        FinishTask("build/compile");
        FinishNode("build");
        Ready();
        StartNode("deploy-eu");
        StartTask("deploy-eu/deploy");
        StartNode("deploy-us");
        StartTask("deploy-us/deploy");
        FinishTask("deploy-eu/deploy");
        FinishNode("deploy-eu");
        FinishTask("deploy-us/deploy");
        FinishNode("deploy-us");
        Ready();
        StartNode("smoke");
        StartTask("smoke/api");
        StartTask("smoke/ui");
        StartTask("smoke/perf-probe");
        FinishTask("smoke/api");
        FinishTask("smoke/ui");
        FinishTask("smoke/perf-probe");
        FinishNode("smoke");
        Ready();
        StartNode("$post");
        StartTask("$post/run");
        FinishTask("$post/run");
        FinishNode("$post");
        Assert.True(StagePlanner.Plan(workflow.Nodes, run.Snapshot.Nodes, run.Snapshot.Status).Complete);
        Emit(EventEntity.Run, null, (int)RunStatus.Succeeded);
        return run;

        void Emit(EventEntity entity, string? key, int status) => run.Append(entity, key, status, DateTimeOffset.UnixEpoch);
        void Ready()
        {
            var stage = StagePlanner.Plan(workflow.Nodes, run.Snapshot.Nodes, run.Snapshot.Status);
            foreach (var change in stage.Changes)
                Emit(EventEntity.Node, change.Node, (int)change.Status);
            foreach (var change in ReadinessEvaluator.Evaluate(workflow.Nodes, run.Snapshot.Nodes, stage.EligibleNodes))
                Emit(EventEntity.Node, change.Node, (int)change.Status);
        }
        void StartNode(string key)
        {
            Emit(EventEntity.Node, key, (int)ExecutionStatus.Dispatched);
            Emit(EventEntity.Node, key, (int)ExecutionStatus.Running);
        }
        void StartTask(string key)
        {
            foreach (var state in new[] { ExecutionStatus.Ready, ExecutionStatus.Dispatched, ExecutionStatus.Running })
                Emit(EventEntity.Task, key, (int)state);
        }
        void FinishTask(string key) => Emit(EventEntity.Task, key, (int)ExecutionStatus.Succeeded);
        void FinishNode(string key)
        {
            var node = workflow.Nodes.Single(n => n.Key == key);
            var outcome = FailurePropagation.ResolveNode(node, node.Tasks.ToDictionary(t => t.Key, t => run.Snapshot.Tasks[$"{key}/{t.Key}"]));
            Assert.NotNull(outcome);
            Emit(EventEntity.Node, key, (int)outcome.Value);
        }
    }

    private static string Format(RunEvent @event) => $"{@event.Entity}{(@event.Key is { } key ? $" {key}" : "")}: " +
        (@event.Entity == EventEntity.Run ? ((RunStatus)@event.State).ToString() : ((ExecutionStatus)@event.State).ToString());

    private sealed class ExecutionSignals : TextWriter
    {
        private readonly Dictionary<string, TaskCompletionSource> signals = new(StringComparer.Ordinal)
        {
            ["Task deploy-us/deploy: Running"] = new(TaskCreationOptions.RunContinuationsAsynchronously),
            ["Node deploy-eu: Succeeded"] = new(TaskCreationOptions.RunContinuationsAsynchronously),
            ["Task smoke/perf-probe: Running"] = new(TaskCreationOptions.RunContinuationsAsynchronously),
            ["Task smoke/api: Succeeded"] = new(TaskCreationOptions.RunContinuationsAsynchronously),
            ["Task smoke/ui: Succeeded"] = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
        public Task WaitAsync(string key, CancellationToken cancellationToken) => signals[key].Task.WaitAsync(cancellationToken);
        public override void WriteLine(string? value)
        {
            if (value is not null && signals.TryGetValue(value, out var signal))
                signal.TrySetResult();
        }
    }
}
