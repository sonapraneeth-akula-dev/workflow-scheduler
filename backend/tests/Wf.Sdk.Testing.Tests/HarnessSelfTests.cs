using System.Collections.Immutable;
using System.Text.Json;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Contracts.Events;
using Wf.Sdk.Hosting;

namespace Wf.Sdk.Testing.Tests;

public sealed class HarnessSelfTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public static IEnumerable<object[]> HarnessSelfPolicyCases()
    {
        foreach (var outcome in Enum.GetValues<MockOutcome>())
        foreach (var nodePolicy in Enum.GetValues<FailurePolicy>())
        foreach (var taskPolicy in Enum.GetValues<FailurePolicy>())
        foreach (var condition in Enum.GetValues<DependencyCondition>())
        foreach (var retries in new[] { 0, 2 })
        foreach (var retryable in new[] { false, true })
            yield return [outcome, nodePolicy, taskPolicy, condition, retries, retryable];
    }

    [Theory]
    [MemberData(nameof(HarnessSelfPolicyCases))]
    public async Task HarnessSelfEveryOutcomeAndFailureDependencyRetryPolicy(MockOutcome outcome, FailurePolicy nodePolicy,
        FailurePolicy taskPolicy, DependencyCondition condition, int retries, bool retryable)
    {
        var workflow = Workflow.Define("matrix", "1", w => w.Type("test")
            .Node("source", n => n.OnFailure(nodePolicy).Task("work", Never,
                p => p.Timeout(TimeSpan.FromMinutes(1)).Retry(retries, Backoff.Fixed(TimeSpan.FromSeconds(1))), taskPolicy))
            .Node("child", n => n.DependsOn("source", condition).Task("work", Never)));
        var spec = Mock(outcome, "source/work", retryable).Succeed("child/work");
        var log = await Run(workflow, spec);
        var canRetry = outcome is MockOutcome.Timeout or MockOutcome.Throw || retryable;
        var succeeds = outcome == MockOutcome.Succeed || outcome == MockOutcome.FailThenSucceed && retries > 0 && canRetry;
        var attemptCount = outcome == MockOutcome.Succeed ? 1 :
            outcome == MockOutcome.FailThenSucceed && succeeds ? 2 : canRetry ? retries + 1 : 1;
        var failedStatus = taskPolicy == FailurePolicy.Soft ? ExecutionStatus.SoftFailed :
            outcome == MockOutcome.Timeout ? ExecutionStatus.TimedOut : ExecutionStatus.Failed;
        var taskStatus = succeeds ? ExecutionStatus.Succeeded : failedStatus;
        var nodeStatus = !succeeds && nodePolicy == FailurePolicy.Soft ? ExecutionStatus.SoftFailed : taskStatus;
        var runStatus = nodeStatus is ExecutionStatus.Failed or ExecutionStatus.TimedOut ? RunStatus.Failed : RunStatus.Succeeded;
        var childRuns = condition == DependencyCondition.Always ||
            condition == DependencyCondition.OnSuccess && nodeStatus is ExecutionStatus.Succeeded or ExecutionStatus.SoftFailed ||
            condition == DependencyCondition.OnFailure && nodeStatus is ExecutionStatus.Failed or ExecutionStatus.TimedOut;
        var attempts = Enumerable.Repeat(failedStatus, attemptCount).ToArray();
        attempts[^1] = taskStatus;
        log.AssertRunStatus(runStatus)
            .AssertNodeStatus("source", nodeStatus)
            .AssertTaskStatus("source/work", taskStatus)
            .AssertRetries("source/work", attemptCount - 1)
            .AssertAttemptStatuses("source/work", attempts)
            .AssertNodeStatus("child", childRuns ? ExecutionStatus.Succeeded : ExecutionStatus.Skipped);
        if (childRuns)
            log.AssertNodeOrder("source", "child").AssertAttemptStatuses("child/work", ExecutionStatus.Succeeded);
        else
            log.AssertSkipped("child");
        if (succeeds)
            log.AssertOutput("source", "payload", Json("""{"answer":42,"items":[true,null,"ok"]}"""));
        else
            log.AssertNoOutputs("source");
        foreach (var attempt in log.Attempts.Where(a => a.TaskKey == "source/work" && a.Status != ExecutionStatus.Succeeded))
        {
            Assert.Equal(outcome == MockOutcome.Timeout ? FailureCategory.Timeout : FailureCategory.TaskError, attempt.Failure);
            if (outcome != MockOutcome.Timeout)
            {
                Assert.Equal(outcome == MockOutcome.Throw ? "InvalidOperationException" : "TaskFailureException", attempt.ErrorType);
                Assert.Equal("expected mock error", attempt.ErrorMessage);
            }
        }
    }

    [Theory]
    [InlineData(FailureCategory.TaskError, true, true, true, 2, true)]
    [InlineData(FailureCategory.TaskError, true, true, false, 0, false)]
    [InlineData(FailureCategory.TaskError, false, true, true, 0, false)]
    [InlineData(FailureCategory.Timeout, true, true, true, 2, true)]
    [InlineData(FailureCategory.Timeout, true, true, false, 0, false)]
    [InlineData(FailureCategory.AgentLost, true, true, false, 2, true)]
    [InlineData(FailureCategory.PlatformError, true, true, false, 2, true)]
    [InlineData(FailureCategory.AgentLost, true, false, false, 0, false)]
    [InlineData(FailureCategory.AgentLost, true, false, true, 2, true)]
    [InlineData(FailureCategory.PlatformError, false, true, true, 0, false)]
    [InlineData(FailureCategory.ValidationError, true, true, true, 0, false)]
    [InlineData(FailureCategory.Aborted, true, true, true, 0, false)]
    [InlineData(FailureCategory.ResourceLimit, true, true, true, 0, false)]
    [InlineData(FailureCategory.Placement, true, true, true, 0, false)]
    public async Task HarnessSelfRetryCategoriesAndBudgets(FailureCategory category, bool retryable,
        bool autoRetry, bool includeCategory, int retries, bool succeeds)
    {
        var workflow = Workflow.Define("budgets", "1", w => w.Type("test").Node("node", n =>
            n.Task("work", Never, p =>
            {
                p.Retry(2, Backoff.Fixed(TimeSpan.FromSeconds(1)), includeCategory ? [category] : []);
                if (!autoRetry)
                    p.NoAutoRetry();
            })));
        var log = await Run(workflow, new MockSpec().FailNThenSucceed("node/work", 2, Outputs(), retryable: retryable, category: category));
        log.AssertRetries("node/work", retries).AssertRunStatus(succeeds ? RunStatus.Succeeded : RunStatus.Failed);
        var failure = category == FailureCategory.Timeout ? ExecutionStatus.TimedOut : ExecutionStatus.Failed;
        log.AssertAttemptStatuses("node/work", succeeds ? [failure, failure, ExecutionStatus.Succeeded] : [failure]);
        Assert.All(log.Attempts.Where(a => a.Status != ExecutionStatus.Succeeded), attempt => Assert.Equal(category, attempt.Failure));
    }

    [Theory]
    [InlineData(0, 0, RunStatus.Succeeded)]
    [InlineData(2, 2, RunStatus.Succeeded)]
    [InlineData(3, 2, RunStatus.Failed)]
    public async Task HarnessSelfRetryBudgetExhaustionDoesNotPublishSuccessOutputs(int failures, int retries, RunStatus status)
    {
        var workflow = Workflow.Define("budget", "1", w => w.Type("test")
            .Node("node", n => n.Task("work", Never, p => p.Retry(2))));
        var log = await Run(workflow, new MockSpec().FailNThenSucceed("node/work", failures, Outputs()));
        log.AssertRetries("node/work", retries).AssertRunStatus(status);
        if (status == RunStatus.Succeeded)
            log.AssertOutput("node", "payload", Json("""{"items":[true,null,"ok"],"answer":42}"""));
        else
            log.AssertNoOutputs("node");
    }

    [Theory]
    [InlineData(FailureCategory.AgentLost)]
    [InlineData(FailureCategory.PlatformError)]
    public async Task HarnessSelfPlatformBudgetPrecedesAndDoesNotConsumeAuthorBudget(FailureCategory category)
    {
        var workflow = Workflow.Define("independent", "1", w => w.Type("test").Node("node", n =>
            n.Task("work", Never, p => p.Retry(2, Backoff.Fixed(TimeSpan.FromSeconds(1)), [category]))));
        var log = await Run(workflow, new MockSpec().FailNThenSucceed("node/work", 4, category: category));
        log.AssertRunStatus(RunStatus.Succeeded).AssertRetries("node/work", 4)
            .AssertAttemptStatuses("node/work", ExecutionStatus.Failed, ExecutionStatus.Failed,
                ExecutionStatus.Failed, ExecutionStatus.Failed, ExecutionStatus.Succeeded);
        Assert.Equal(new[] { 0d, 0d, 0d, 1d, 2d }, log.Attempts.Select(a => (a.StartedAt - DateTimeOffset.UnixEpoch).TotalSeconds));
    }

    [Theory]
    [InlineData(FailurePolicy.Hard, false)]
    [InlineData(FailurePolicy.Soft, false)]
    [InlineData(FailurePolicy.Hard, true)]
    [InlineData(FailurePolicy.Soft, true)]
    public async Task HarnessSelfSiblingPropagationInSeriesAndParallel(FailurePolicy failure, bool parallel)
    {
        var workflow = Workflow.Define("siblings", "1", w => w.Type("test").Node("node", n =>
        {
            if (parallel)
                n.Parallel(1);
            n.Task("first", Never, failure).Task("second", Never);
        }));
        var log = await Run(workflow, new MockSpec().Fail("node/first").Succeed("node/second"));
        log.AssertTaskStatus("node/first", failure == FailurePolicy.Soft ? ExecutionStatus.SoftFailed : ExecutionStatus.Failed)
            .AssertTaskStatus("node/second", failure == FailurePolicy.Soft ? ExecutionStatus.Succeeded : ExecutionStatus.Cancelled)
            .AssertRetries("node/second", 0)
            .AssertRunStatus(failure == FailurePolicy.Soft ? RunStatus.Succeeded : RunStatus.Failed);
        log.AssertAttemptStatuses("node/second", failure == FailurePolicy.Soft ? [ExecutionStatus.Succeeded] : []);
    }

    [Theory]
    [InlineData("$init", FailurePolicy.Hard)]
    [InlineData("$pre", FailurePolicy.Hard)]
    [InlineData("body", FailurePolicy.Hard)]
    [InlineData("$post", FailurePolicy.Hard)]
    [InlineData("$sys-post", FailurePolicy.Hard)]
    [InlineData("$init", FailurePolicy.Soft)]
    [InlineData("$pre", FailurePolicy.Soft)]
    [InlineData("body", FailurePolicy.Soft)]
    [InlineData("$post", FailurePolicy.Soft)]
    [InlineData("$sys-post", FailurePolicy.Soft)]
    public async Task HarnessSelfLifecycleFailuresAlwaysReachBothPostStages(string failedNode, FailurePolicy policy)
    {
        var workflow = Workflow.Define("stages", "1", w => w.Type("test")
            .SystemInit(s => s.Task("run", Never, policy))
            .PreSetup(s => s.Task("run", Never, policy))
            .Node("body", n => n.Task("run", Never, policy))
            .PostProcess(s => s.Task("run", Never, policy))
            .SystemPostProcess(s => s.Task("run", Never, policy)));
        var spec = new MockSpec(new TaskMock()).Fail(failedNode + "/run");
        var log = await Run(workflow, spec);
        var failed = policy == FailurePolicy.Hard ? ExecutionStatus.Failed : ExecutionStatus.SoftFailed;
        log.AssertRunStatus(policy == FailurePolicy.Hard ? RunStatus.Failed : RunStatus.Succeeded)
            .AssertNodeStatus(failedNode, failed)
            .AssertStageStatus("$post", failedNode == "$post" ? failed : ExecutionStatus.Succeeded)
            .AssertStageStatus("$sys-post", failedNode == "$sys-post" ? failed : ExecutionStatus.Succeeded)
            .AssertStageOrder("$post", "$sys-post");
        if (policy == FailurePolicy.Hard && failedNode is "$init" or "$pre")
        {
            log.AssertSkipped("body");
            if (failedNode == "$init")
                log.AssertSkipped("$pre");
            log.AssertNodeOrder(failedNode, "$post", "$sys-post");
        }
        else
            log.AssertNodeOrder("$init", "$pre", "body", "$post", "$sys-post")
                .AssertStageOrder("$init", "$pre", "$post", "$sys-post");
    }

    [Fact]
    public async Task HarnessSelfFluentAssertionsPreserveLogAndTypedStructuredOutputs()
    {
        var log = await Success();
        Assert.Same(log, log.AssertRunStatus(RunStatus.Succeeded).AssertNodeStatus("first", ExecutionStatus.Succeeded)
            .AssertTaskStatus("first/work", ExecutionStatus.Succeeded).AssertNodeOrder("first", "second")
            .AssertRetries("first/work", 0).AssertAttemptStatuses("first/work", ExecutionStatus.Succeeded)
            .AssertOutput("first", "payload", Json("""{"items":[true,null,"ok"],"answer":42}"""))
            .AssertNoOutputs("second"));
        var typed = await Run(OneTask(), new MockSpec().Succeed("node/work", new Dictionary<string, JsonElement>
        {
            ["null"] = Json("null"), ["string"] = Json("\"42\""), ["number"] = Json("42"),
            ["bool"] = Json("true"), ["array"] = Json("[1,2]"), ["object"] = Json("""{"x":1}""")
        }));
        foreach (var (key, value) in typed.Outputs["node"])
            typed.AssertOutput("node", key, value);
        Failure(() => typed.AssertOutput("node", "null", Json("false")), "Null");
        Failure(() => typed.AssertOutput("node", "string", Json("42")), "String");
        Failure(() => typed.AssertOutput("node", "number", Json("\"42\"")), "Number");
        Failure(() => typed.AssertOutput("node", "bool", Json("\"true\"")), "True");
        Failure(() => typed.AssertOutput("node", "array", Json("[2,1]")), "Array");
        Failure(() => typed.AssertOutput("node", "object", Json("""{"x":2}""")), "Object");
    }

    [Fact]
    public async Task HarnessSelfFailedAssertionsIdentifyExpectedActualAndUnknownOrMissingKeys()
    {
        var log = await Success();
        Failure(() => log.AssertRunStatus(RunStatus.Failed), "expected Failed, actual Succeeded");
        Failure(() => log.AssertNodeStatus("first", ExecutionStatus.Failed), "Node 'first'");
        Failure(() => log.AssertTaskStatus("first/work", ExecutionStatus.TimedOut), "Task 'first/work'");
        Failure(() => log.AssertSkipped("first"), "Skipped");
        Failure(() => log.AssertNodeOrder("second", "first"), "completion sequence");
        Failure(() => log.AssertRetries("first/work", 1), "retry events: expected 1, actual 0");
        Failure(() => log.AssertAttemptStatuses("first/work", ExecutionStatus.Failed), "persisted attempt statuses");
        Failure(() => log.AssertAttemptStatuses("first/work"), "persisted attempt statuses");
        Failure(() => log.AssertStageStatus("first"), "Unknown lifecycle stage");
        Failure(() => log.AssertStageStatus("$post"), "$post");
        Failure(() => log.AssertStageOrder("$init", "first"), "Unknown lifecycle stage");
        Failure(() => log.AssertNoOutputs("first"), "expected no outputs");
        Failure(() => log.AssertOutput("first", "absent", Json("null")), "missing output 'absent'");
        Failure(() => log.AssertOutput("first", "payload", Json("null")), "expected Null null, actual Object");
        Failure(() => log.AssertNodeStatus("FIRST", ExecutionStatus.Succeeded), "Unknown Node key 'FIRST'");
        Failure(() => log.AssertTaskStatus("first/absent", ExecutionStatus.Succeeded), "Available keys:");
        Failure(() => log.AssertRetries("first/absent", 0), "first/absent");
        Failure(() => log.AssertSkipped("absent"), "absent");
        Failure(() => log.AssertNodeOrder("first", "absent"), "absent");
        Failure(() => log.AssertOutput("absent", "value", Json("null")), "Unknown Node key");
        Failure(() => log.AssertNoOutputs("absent"), "Unknown Node key");
        var absentOutputs = log with { Outputs = log.Outputs.Remove("second") };
        Failure(() => absentOutputs.AssertNoOutputs("second"), "no persisted output collection");
        Failure(() => absentOutputs.AssertOutput("second", "null", Json("null")), "missing output");
    }

    [Fact]
    public async Task HarnessSelfAssertionsRejectInvalidArguments()
    {
        var log = await Success();
        Assert.Throws<ArgumentNullException>(() => RunAssertions.AssertRunStatus(null!, RunStatus.Succeeded));
        Assert.Throws<ArgumentOutOfRangeException>(() => log.AssertRunStatus((RunStatus)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => log.AssertNodeStatus("first", (ExecutionStatus)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => log.AssertTaskStatus("first/work", (ExecutionStatus)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => log.AssertAttemptStatuses("first/work", (ExecutionStatus)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => log.AssertRetries("first/work", -1));
        Assert.Throws<ArgumentException>(() => log.AssertOutput("first", "payload", default));
        Assert.Throws<ArgumentException>(() => log.AssertOutput("first", " ", Json("null")));
        Assert.Throws<ArgumentException>(() => log.AssertNodeStatus(" ", ExecutionStatus.Succeeded));
        Assert.Throws<ArgumentException>(() => log.AssertStageStatus(" "));
        Assert.Throws<ArgumentException>(() => log.AssertNodeOrder());
        Assert.Throws<ArgumentException>(() => log.AssertSkipped());
        Assert.Throws<ArgumentException>(() => log.AssertNodeOrder("first", "first"));
        Assert.Throws<ArgumentException>(() => log.AssertNodeOrder("first", " "));
        Assert.Throws<ArgumentNullException>(() => log.AssertNodeOrder(null!));
        Assert.Throws<ArgumentNullException>(() => log.AssertStageOrder(null!));
        Assert.Throws<ArgumentNullException>(() => log.AssertAttemptStatuses("first/work", null!));
    }

    [Fact]
    public async Task HarnessSelfTamperedEventJournalsAndSnapshotsNeverProducePassingAssertions()
    {
        var log = await Success();
        Failure(() => (log with { Events = default }).AssertRunStatus(RunStatus.Succeeded), "initialized events");
        Failure(() => (log with { Attempts = default }).AssertRunStatus(RunStatus.Succeeded), "initialized events");
        Failure(() => (log with { Events = log.Events.RemoveAt(0) }).AssertRunStatus(RunStatus.Succeeded), "sequence");
        Failure(() => (log with { Events = log.Events.Reverse().ToImmutableArray() }).AssertNodeOrder("first", "second"), "sequence");
        Failure(() => (log with { Events = log.Events.SetItem(0, log.Events[0] with { RunId = Guid.NewGuid() }) })
            .AssertRunStatus(RunStatus.Succeeded), "same run");
        Failure(() => (log with { Events = log.Events.SetItem(0, log.Events[0] with { State = 99 }) })
            .AssertRunStatus(RunStatus.Succeeded), "transition");
        var taskEvent = log.Events.First(e => e.Entity == EventEntity.Task);
        Failure(() => (log with { Events = log.Events.SetItem(log.Events.IndexOf(taskEvent), taskEvent with { Key = "absent/work" }) })
            .AssertTaskStatus("first/work", ExecutionStatus.Succeeded), "Unknown Task key");
        Failure(() => (log with { Snapshot = log.Snapshot with { Status = RunStatus.Failed } })
            .AssertRunStatus(RunStatus.Failed), "event/snapshot status");
        Failure(() => (log with { Snapshot = log.Snapshot with { Nodes = log.Snapshot.Nodes.SetItem("first", ExecutionStatus.Failed) } })
            .AssertNodeStatus("first", ExecutionStatus.Failed), "event/snapshot status");
        Failure(() => (log with { Snapshot = log.Snapshot with { Tasks = log.Snapshot.Tasks.SetItem("first/work", ExecutionStatus.Failed) } })
            .AssertTaskStatus("first/work", ExecutionStatus.Failed), "event/snapshot status");
        Failure(() => (log with { Snapshot = log.Snapshot with { LastSequence = 999 } })
            .AssertRunStatus(RunStatus.Succeeded), "last sequence");
        Failure(() => (log with { Snapshot = log.Snapshot with { EndedAt = DateTimeOffset.UnixEpoch.AddDays(1) } })
            .AssertRunStatus(RunStatus.Succeeded), "end time");
        Failure(() => (log with { Attempts = log.Attempts.Add(log.Attempts[0] with { TaskKey = "absent/work" }) })
            .AssertRunStatus(RunStatus.Succeeded), "Unknown Task key");
        Failure(() => (log with { Outputs = log.Outputs.Add("absent", ImmutableDictionary<string, JsonElement>.Empty) })
            .AssertRunStatus(RunStatus.Succeeded), "Unknown Node key");
        Failure(() => (log with { Outputs = log.Outputs.SetItem("first", log.Outputs["first"].SetItem("payload", default)) })
            .AssertOutput("first", "payload", Json("null")), "Undefined");
    }

    [Fact]
    public async Task HarnessSelfRetriesRequireActualAttemptRowsNumbersOutcomesAndMatchingEvents()
    {
        var workflow = Workflow.Define("retry", "1", w => w.Type("test")
            .Node("node", n => n.Task("work", Never, p => p.Retry(2))));
        var log = await Run(workflow, new MockSpec().FailNThenSucceed("node/work", 2));
        log.AssertRetries("node/work", 2).AssertAttemptStatuses("node/work",
            ExecutionStatus.Failed, ExecutionStatus.Failed, ExecutionStatus.Succeeded);
        Failure(() => (log with { Attempts = [] }).AssertRetries("node/work", 2), "executed retries");
        Failure(() => (log with { Attempts = log.Attempts.RemoveAt(1) }).AssertRetries("node/work", 2), "executed retries");
        Failure(() => (log with { Attempts = log.Attempts.SetItem(1, log.Attempts[1] with { Attempt = 7 }) })
            .AssertRetries("node/work", 2), "expected 2, actual 7");
        Failure(() => (log with { Attempts = log.Attempts.SetItem(0, log.Attempts[0] with { StartedAt = DateTimeOffset.UnixEpoch.AddHours(1) }) })
            .AssertRetries("node/work", 2), "timestamps");
        Failure(() => (log with { Attempts = log.Attempts.SetItem(0, log.Attempts[0] with { EndedAt = DateTimeOffset.UnixEpoch.AddHours(1) }) })
            .AssertRetries("node/work", 2), "timestamps");
        Failure(() => (log with { Attempts = log.Attempts.SetItem(0, log.Attempts[0] with { Status = ExecutionStatus.Succeeded }) })
            .AssertRetries("node/work", 2), "invalid retry outcome");
        Failure(() => (log with { Attempts = log.Attempts.SetItem(0, log.Attempts[0] with { Failure = null }) })
            .AssertRetries("node/work", 2), "invalid retry outcome");
        Failure(() => (log with { Attempts = log.Attempts.SetItem(2, log.Attempts[2] with { Status = ExecutionStatus.Failed }) })
            .AssertRetries("node/work", 2), "final persisted attempt status");
        var success = await Success();
        Failure(() => (success with { Attempts = [] }).AssertRetries("first/work", 0), "no persisted attempts");
        var skipped = await Run(Workflow.Define("skip", "1", w => w.Type("test")
                .Node("source", n => n.Task("work", Never)).Node("child", n => n.DependsOn("source").Task("work", Never))),
            new MockSpec().Fail("source/work").Succeed("child/work"));
        skipped.AssertSkipped("child");
        Failure(() => skipped.AssertNodeOrder("source", "child"), "did not execute");
        Failure(() => (skipped with { Attempts = skipped.Attempts.Add(skipped.Attempts[0] with { TaskKey = "child/work" }) })
            .AssertSkipped("child"), "attempt events");
    }

    private static async Task<DryRunLog> Success() => await Run(Workflow.Define("success", "1", w => w.Type("test")
        .Node("first", n => n.Task("work", Never))
        .Node("second", n => n.DependsOn("first").Task("work", Never))),
        new MockSpec().Succeed("first/work", Outputs()).Succeed("second/work"));

    private static WorkflowDefinition OneTask() => Workflow.Define("one", "1", w => w.Type("test")
        .Node("node", n => n.Task("work", Never)));

    private static Task<DryRunLog> Run(WorkflowDefinition workflow, MockSpec spec) =>
        DryRunHost.ExecuteAsync(workflow, spec.ToDryRunSpec(), Token);

    private static MockSpec Mock(MockOutcome outcome, string key, bool retryable) => outcome switch
    {
        MockOutcome.Succeed => new MockSpec().Succeed(key, Outputs()),
        MockOutcome.Fail => new MockSpec().Fail(key, "expected mock error", retryable),
        MockOutcome.Timeout => new MockSpec().Timeout(key),
        MockOutcome.Throw => new MockSpec().Throw(key, "expected mock error"),
        MockOutcome.FailThenSucceed => new MockSpec().FailNThenSucceed(key, 1, Outputs(), "expected mock error", retryable),
        _ => throw new ArgumentOutOfRangeException(nameof(outcome))
    };

    private static Dictionary<string, JsonElement> Outputs() =>
        new() { ["payload"] = Json("""{"answer":42,"items":[true,null,"ok"]}""") };

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static void Failure(Action assertion, string detail) =>
        Assert.Contains(detail, Assert.Throws<RunAssertionException>(assertion).Message, StringComparison.Ordinal);

    private static void Never(TaskContext _) => throw new InvalidOperationException("Real task delegates must not execute.");
}
