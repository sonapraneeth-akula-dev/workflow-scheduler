using System.Collections.Immutable;
using System.Text.Json;
using Wf.Contracts;
using Wf.Engine.Core;
using Wf.Sdk;
using Wf.Sdk.Hosting;

namespace Wf.Integration.Tests.Execution;

public sealed class DryRunHostTests
{
    [Fact]
    public async Task ParallelTimeoutRetryOutputsAndCleanupLogRoundTripReplaysThroughCore()
    {
        var calls = 0;
        var workflow = Workflow.Define("dry-integration", "1", w => w.Type("test").Metadata("area", "harness")
            .Node("parallel", n => n.Parallel(2)
                .Task("timeout", _ => { calls++; }, failure: Failure.Soft)
                .Task("success", _ => { calls++; })
                .Task("retry", _ => { calls++; }, p => p.Retry(2, Backoff.Exponential(TimeSpan.FromMinutes(1)))))
            .Node("child", n => n.DependsOn("parallel").Task("work", _ => { calls++; }))
            .PostProcess(_ => { calls++; }));
        var spec = new DryRunSpec(new Dictionary<string, TaskMock>
        {
            ["parallel/timeout"] = new(MockOutcome.Timeout),
            ["parallel/retry"] = new(MockOutcome.FailThenSucceed, FailuresBeforeSuccess: 2),
            ["parallel/success"] = new(Outputs: new Dictionary<string, JsonElement>
                { ["answer"] = JsonSerializer.SerializeToElement(42) }.ToImmutableDictionary())
        }.ToImmutableDictionary(), new());
        var live = await DryRunHost.ExecuteAsync(workflow, spec, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var restoredLog = JsonSerializer.Deserialize(JsonSerializer.Serialize(live, DryRunJsonContext.Default.DryRunLog),
            DryRunJsonContext.Default.DryRunLog)!;
        var initial = new RunState(restoredLog.Snapshot.RunId, workflow.Nodes, workflow.Metadata, Assert.Fail,
            restoredLog.Snapshot.Variables).Snapshot;
        var replay = RunState.Restore(initial, restoredLog.Events, Assert.Fail);
        Assert.Equal(0, calls);
        Assert.Equal(RunStatus.Succeeded, replay.Snapshot.Status);
        Assert.Equal(ExecutionStatus.SoftFailed, replay.Snapshot.Nodes["parallel"]);
        Assert.Equal(ExecutionStatus.Succeeded, replay.Snapshot.Nodes["child"]);
        Assert.Equal(ExecutionStatus.Succeeded, replay.Snapshot.Nodes["$post"]);
        Assert.Equal(live.Events, replay.Events);
        Assert.Equal(live.Snapshot.LastSequence, replay.Snapshot.LastSequence);
        Assert.Equal(live.Snapshot.EndedAt, replay.Snapshot.EndedAt);
        Assert.Equal(3, restoredLog.Attempts.Count(a => a.TaskKey == "parallel/retry"));
        Assert.Equal(42, restoredLog.Outputs["parallel"]["answer"].GetInt32());
        Assert.Equal("TaskFailureException", restoredLog.Attempts.First(a => a.TaskKey == "parallel/retry").ErrorType);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddHours(1), replay.Snapshot.EndedAt);
    }
}
