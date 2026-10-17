using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Contracts.Events;
using Wf.Sdk;
using Wf.Sdk.Local;

namespace Wf.Sdk.Tests;

public sealed class LocalEngineTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(0.25, 0UL)]
    [InlineData(0, 192UL)]
    public void Real_in_process_execution_rejects_caps_instead_of_silently_ignoring_them(double cpu, ulong ram)
    {
        var calls = 0;
        var workflow = Workflow.Define("limited", "1", w => w.Type("test")
            .Node("work", n => n.Task("task", _ => { calls++; }, p => p.Limits(cpu, ram))));
        var error = Assert.Throws<NotSupportedException>(() => new LocalEngine(workflow));
        Assert.Contains("require agent execution", error.Message);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task StageAndSeriesOrderVariablesMetadataAndOutputsReachAnActualRun()
    {
        var order = new List<string>();
        var workflow = Workflow.Define("local", "1", w => w.Type("test").Metadata("area", "checkout")
            .Variables(v => v.String("env", "default"))
            .SystemPostProcess(_ => { order.Add("sys-post"); })
            .PostProcess(_ => { order.Add("post"); })
            .PreSetup(ctx => { Assert.Equal("dev", ctx.Var<string>("env")); order.Add("pre"); })
            .SystemInit(_ => { order.Add("init"); })
            .Node("producer", n => n.Task("one", ctx => { order.Add("one"); ctx.Outputs["answer"] = 42; })
                .Task("two", _ => { order.Add("two"); }))
            .Node("consumer", n => n.DependsOn("producer").Input("producer", "answer")
                .Task("read", ctx => { Assert.Equal(42, ctx.Input<int>("producer", "answer")); order.Add("read"); })));
        using var output = new StringWriter();
        var result = await new LocalEngine(workflow, LocalVariables.Parse(workflow.Variables, ["--var", "env=dev"]), output)
            .RunAsync(Token);
        Assert.Equal(["init", "pre", "one", "two", "read", "post", "sys-post"], order);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(RunStatus.Succeeded, result.Snapshot.Status);
        Assert.Equal("test", result.Snapshot.Metadata.Type);
        Assert.Equal("checkout", result.Snapshot.Metadata.Values["area"]);
        Assert.Equal("dev", result.Snapshot.Variables["env"].GetString());
        Assert.Equal(42, result.Outputs["producer"]["answer"].GetInt32());
        Assert.Contains("Task producer/one: Running", output.ToString());
        Assert.Contains("Run: Succeeded", output.ToString());
        AssertTerminalConservation(result);
    }

    [Fact]
    public async Task ParallelTasksRespectCapAndIndependentBranchesOverlap()
    {
        var bothStarted = Signal();
        var release = Signal();
        var current = 0;
        var max = 0;
        var branchStarted = Signal();
        var workflow = Workflow.Define("parallel", "1", w => w.Type("test")
            .Node("parallel", n =>
            {
                n.Parallel(2);
                for (var index = 0; index < 6; index++)
                    n.Task($"task-{index}", async ctx =>
                    {
                        var count = Interlocked.Increment(ref current);
                        lock (bothStarted) max = Math.Max(max, count);
                        if (count == 2)
                            bothStarted.TrySetResult();
                        await release.Task.WaitAsync(ctx.CancellationToken);
                        Interlocked.Decrement(ref current);
                    });
            })
            .Node("branch", n => n.Task("work", async ctx =>
            {
                branchStarted.TrySetResult();
                await release.Task.WaitAsync(ctx.CancellationToken);
            })));
        var execution = new LocalEngine(workflow, output: TextWriter.Null).RunAsync(Token);
        try
        {
            await Task.WhenAll(bothStarted.Task, branchStarted.Task).WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.Equal(2, Volatile.Read(ref current));
        }
        finally { release.TrySetResult(); }
        Assert.Equal(0, (await execution.WaitAsync(TimeSpan.FromSeconds(10), Token)).ExitCode);
        Assert.Equal(2, max);
    }

    [Fact]
    public async Task HardFailureCancelsActiveSiblingAndDoesNotStartWaitingSibling()
    {
        var siblingStarted = Signal();
        var pendingRan = false;
        var cleanupRan = false;
        var workflow = Workflow.Define("hard", "1", w => w.Type("test")
            .Node("node", n => n.Parallel(2)
                .Task("fail", async ctx =>
                {
                    await siblingStarted.Task.WaitAsync(ctx.CancellationToken);
                    throw new NonRetryableException("expected");
                })
                .Task("wait", async ctx =>
                {
                    siblingStarted.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, ctx.CancellationToken);
                })
                .Task("pending", _ => { pendingRan = true; }))
            .Node("child", n => n.DependsOn("node").Task("never", _ => { pendingRan = true; }))
            .PostProcess(_ => { cleanupRan = true; }));
        var result = await new LocalEngine(workflow, output: TextWriter.Null).RunAsync(Token)
            .WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(RunStatus.Failed, result.Snapshot.Status);
        Assert.Equal(ExecutionStatus.Cancelled, result.Snapshot.Tasks["node/wait"]);
        Assert.Equal(ExecutionStatus.Cancelled, result.Snapshot.Tasks["node/pending"]);
        Assert.Equal(ExecutionStatus.Skipped, result.Snapshot.Tasks["child/never"]);
        Assert.False(pendingRan);
        Assert.True(cleanupRan);
        AssertTerminalConservation(result);
    }

    [Fact]
    public async Task FailureBranchesAndAlwaysRunWhileDefaultChildSkips()
    {
        var reached = new HashSet<string>();
        var workflow = Workflow.Define("branches", "1", w => w.Type("test")
            .Node("fail", n => n.Task("work", _ => throw new NonRetryableException("expected")))
            .Node("success", n => n.DependsOn("fail").Task("work", _ => { lock (reached) reached.Add("success"); }))
            .Node("failure", n => n.DependsOn("fail", DependencyCondition.OnFailure)
                .Task("work", _ => { lock (reached) reached.Add("failure"); }))
            .Node("always", n => n.DependsOn("fail", DependencyCondition.Always)
                .Task("work", _ => { lock (reached) reached.Add("always"); })));
        var result = await new LocalEngine(workflow, output: TextWriter.Null).RunAsync(Token);
        Assert.Equal(RunStatus.Failed, result.Snapshot.Status);
        Assert.Equal(["always", "failure"], reached.Order());
        AssertTerminalConservation(result);
    }

    [Fact]
    public async Task SoftTaskAndNodeFailureContinueSuccessPath()
    {
        var next = false;
        var downstream = false;
        var workflow = Workflow.Define("soft", "1", w => w.Type("test")
            .Node("soft-task", n => n.Task("fail", _ => throw new NonRetryableException("soft task"), failure: Failure.Soft)
                .Task("continue", _ => { next = true; }))
            .Node("soft-node", n => n.DependsOn("soft-task").OnFailure(Failure.Soft)
                .Task("fail", _ => throw new NonRetryableException("soft node")))
            .Node("downstream", n => n.DependsOn("soft-node").Task("work", _ => { downstream = true; })));
        var result = await new LocalEngine(workflow, output: TextWriter.Null).RunAsync(Token);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(ExecutionStatus.SoftFailed, result.Snapshot.Nodes["soft-node"]);
        Assert.True(next && downstream);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuthorAndPlatformRetryBudgetsReachRealDelegates(bool platform)
    {
        var calls = 0;
        var workflow = Workflow.Define("retry", "1", w => w.Type("test").Node("node", n => n
            .Task("work", _ =>
            {
                if (Interlocked.Increment(ref calls) < 3)
                    throw new TaskFailureException("transient", category: platform ? FailureCategory.AgentLost : FailureCategory.TaskError);
            }, p =>
            {
                if (!platform)
                    p.Retry(2, Backoff.Fixed(TimeSpan.Zero)).NoAutoRetry();
            })));
        var result = await new LocalEngine(workflow, output: TextWriter.Null).RunAsync(Token);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(3, calls);
        Assert.Equal([1, 2, 3], result.Attempts.Select(a => a.Attempt));
        Assert.Equal(2, result.Events.Count(e => e.Entity == EventEntity.Task && e.State == (int)ExecutionStatus.Retrying));
        AssertTerminalConservation(result);
    }

    [Fact]
    public async Task NoAutoRetryAndPermanentErrorsStopAfterOneAttempt()
    {
        foreach (var permanent in new[] { false, true })
        {
            var workflow = Workflow.Define("stop", "1", w => w.Type("test").Node("node", n =>
                n.Task("work", _ => throw (permanent ? new NonRetryableException("permanent") :
                    new TaskFailureException("agent lost", category: FailureCategory.AgentLost)),
                    p => p.Retry(2, Backoff.Fixed(TimeSpan.Zero)).NoAutoRetry())));
            var result = await new LocalEngine(workflow, output: TextWriter.Null).RunAsync(Token);
            Assert.Equal(1, result.ExitCode);
            Assert.Single(result.Attempts);
        }
    }

    [Fact]
    public async Task AttemptTimeoutCancelsAndRetriesBeforeSuccessfulAttempt()
    {
        var clock = new ManualTimeProvider();
        var started = Signal();
        var calls = 0;
        var workflow = Workflow.Define("timeout", "1", w => w.Type("test").RunTimeout(TimeSpan.FromSeconds(10))
            .Node("node", n => n.Task("work", async ctx =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    started.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, ctx.CancellationToken);
                }
            }, p => p.Timeout(TimeSpan.FromMilliseconds(60)).Retry(1, Backoff.Fixed(TimeSpan.Zero)))));
        var execution = new LocalEngine(workflow, output: TextWriter.Null, timeProvider: clock).RunAsync(Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        clock.Advance(TimeSpan.FromMilliseconds(60));
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(2, calls);
        Assert.Equal(FailureCategory.Timeout, result.Attempts[0].Failure);
        Assert.Equal(ExecutionStatus.TimedOut, result.Attempts[0].Status);
    }

    [Fact]
    public async Task NodeDeadlineStopsBackoffWithoutAnotherAttempt()
    {
        var clock = new ManualTimeProvider();
        using var output = new SignalWriter("Task node/work: Retrying");
        var workflow = Workflow.Define("node-timeout", "1", w => w.Type("test")
            .Node("node", n => n.Timeout(TimeSpan.FromSeconds(2)).Task("work",
                _ => throw new InvalidOperationException("transient"),
                p => p.Timeout(TimeSpan.FromMilliseconds(50)).Retry(2, Backoff.Fixed(TimeSpan.FromSeconds(1))))));
        var execution = new LocalEngine(workflow, output: output, timeProvider: clock).RunAsync(Token);
        await output.Seen.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        clock.Advance(TimeSpan.FromSeconds(3));
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Equal(RunStatus.Failed, result.Snapshot.Status);
        Assert.Equal(ExecutionStatus.TimedOut, result.Snapshot.Nodes["node"]);
        Assert.Single(result.Attempts);
        AssertTerminalConservation(result);
    }

    [Fact]
    public async Task RunDeadlineStillAllowsPostStageWithItsOwnTimeout()
    {
        var clock = new ManualTimeProvider();
        using var output = new SignalWriter("Task node/work: Retrying");
        var cleanup = false;
        var workflow = Workflow.Define("run-timeout", "1", w => w.Type("test")
            .RunTimeout(TimeSpan.FromSeconds(2))
            .Node("node", n => n.Task("work", _ => throw new InvalidOperationException("transient"),
                p => p.Timeout(TimeSpan.FromMilliseconds(50)).Retry(10, Backoff.Fixed(TimeSpan.FromSeconds(1)))))
            .PostProcess(s => s.Timeout(TimeSpan.FromSeconds(1)).Task("cleanup", ctx =>
            {
                Assert.Null(ctx.Abort);
                cleanup = true;
            })));
        var execution = new LocalEngine(workflow, output: output, timeProvider: clock).RunAsync(Token);
        await output.Seen.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        clock.Advance(TimeSpan.FromSeconds(3));
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Equal(RunStatus.TimedOut, result.Snapshot.Status);
        Assert.Equal(1, result.ExitCode);
        Assert.True(cleanup);
        Assert.Equal(ExecutionStatus.Succeeded, result.Snapshot.Nodes["$post"]);
    }

    [Fact]
    public async Task AbortBeforeStartSkipsEverythingAndFirstRequestWins()
    {
        var called = false;
        var workflow = Workflow.Define("queued", "1", w => w.Type("test")
            .Node("node", n => n.Task("never", _ => { called = true; })).PostProcess(_ => { called = true; }));
        var engine = new LocalEngine(workflow, output: TextWriter.Null);
        var record = AbortRecord();
        var accepted = engine.AbortAsync(record);
        var result = await engine.RunAsync(Token);
        Assert.Equal(record, await accepted);
        Assert.Equal(record, await engine.AbortAsync(AbortRecord("second")));
        Assert.Equal(RunStatus.Aborted, result.Snapshot.Status);
        Assert.False(called);
        AssertTerminalConservation(result);
        Assert.Single(result.Events, e => e.Abort is not null);
    }

    [Fact]
    public async Task RunningAbortDrainsAndPassesRecordToAlwaysAndPostStages()
    {
        var started = Signal();
        var cleanup = new List<AbortRecord?>();
        var workflow = Workflow.Define("abort", "1", w => w.Type("test")
            .Node("body", n => n.Task("wait", async ctx =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ctx.CancellationToken);
            }))
            .Node("always", n => n.DependsOn("body", DependencyCondition.Always)
                .Task("cleanup", ctx => { cleanup.Add(ctx.Abort); }))
            .PostProcess(ctx => { cleanup.Add(ctx.Abort); })
            .SystemPostProcess(ctx => { cleanup.Add(ctx.Abort); }));
        var engine = new LocalEngine(workflow, output: TextWriter.Null);
        var execution = engine.RunAsync(Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        var record = AbortRecord();
        Assert.Equal(record, await engine.AbortAsync(record));
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Equal(RunStatus.Aborted, result.Snapshot.Status);
        Assert.Equal(ExecutionStatus.Cancelled, result.Snapshot.Tasks["body/wait"]);
        Assert.Equal(3, cleanup.Count);
        Assert.All(cleanup, value => Assert.Equal(record, value));
        Assert.True(result.Snapshot.EndedAt >= record.At);
        AssertTerminalConservation(result);
    }

    [Fact]
    public async Task AbortDuringPostProcessCancelsItButRunsSystemPostProcess()
    {
        var started = Signal();
        AbortRecord? seen = null;
        var workflow = Workflow.Define("abort-post", "1", w => w.Type("test")
            .PostProcess(async ctx =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ctx.CancellationToken);
            })
            .SystemPostProcess(ctx => { seen = ctx.Abort; }));
        var engine = new LocalEngine(workflow, output: TextWriter.Null);
        var execution = engine.RunAsync(Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        var record = AbortRecord();
        await engine.AbortAsync(record);
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Equal(RunStatus.Aborted, result.Snapshot.Status);
        Assert.Equal(ExecutionStatus.Cancelled, result.Snapshot.Nodes["$post"]);
        Assert.Equal(record, seen);
        AssertTerminalConservation(result);
    }

    [Fact]
    public async Task AbortDuringBackoffDisarmsRetryAndDuplicateRequestsKeepOriginalRecord()
    {
        var clock = new ManualTimeProvider();
        using var output = new SignalWriter("Task body/work: Retrying");
        var workflow = Workflow.Define("abort-backoff", "1", w => w.Type("test")
            .Node("body", n => n.Task("work", _ => throw new InvalidOperationException("retry"),
                p => p.Retry(2, Backoff.Fixed(TimeSpan.FromMinutes(1))))));
        var engine = new LocalEngine(workflow, output: output, timeProvider: clock);
        var execution = engine.RunAsync(Token);
        await output.Seen.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        var original = new AbortRecord("local", "Local", clock.GetUtcNow(), "Backoff abort", AbortSource.Local);
        var accepted = engine.AbortAsync(original);
        var duplicate = engine.AbortAsync(new("second", "Second", clock.GetUtcNow(), "Duplicate", AbortSource.API));
        Assert.Equal(original, await accepted.WaitAsync(TimeSpan.FromSeconds(10), Token));
        Assert.Equal(original, await duplicate.WaitAsync(TimeSpan.FromSeconds(10), Token));
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Equal(RunStatus.Aborted, result.Snapshot.Status);
        Assert.Single(result.Attempts);
        Assert.Single(result.Events, e => e.Abort is not null);
        AssertTerminalConservation(result);
    }

    [Fact]
    public async Task SoftTimeoutContinuesSeriesAndTaskSelfCancellationCannotSucceedRun()
    {
        var continued = false;
        var workflow = Workflow.Define("soft-timeout", "1", w => w.Type("test").Node("node", n =>
            n.Task("timeout", ctx => Task.Delay(Timeout.InfiniteTimeSpan, ctx.CancellationToken),
                p => p.Timeout(TimeSpan.FromMilliseconds(40)), Failure.Soft)
                .Task("next", _ => { continued = true; })));
        var result = await new LocalEngine(workflow, output: TextWriter.Null).RunAsync(Token)
            .WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(ExecutionStatus.SoftFailed, result.Snapshot.Tasks["node/timeout"]);
        Assert.True(continued);

        var afterCancellation = false;
        var selfCancelled = Workflow.Define("cancelled-task", "1", w => w.Type("test").Node("node", n =>
            n.Task("cancel", _ => Task.FromCanceled(new CancellationToken(true)), p => p.Retry(3))
                .Task("never", _ => { afterCancellation = true; })));
        var cancelled = await new LocalEngine(selfCancelled, output: TextWriter.Null).RunAsync(Token);
        Assert.Equal(1, cancelled.ExitCode);
        Assert.Equal(RunStatus.Failed, cancelled.Snapshot.Status);
        Assert.Equal(4, cancelled.Attempts.Length);
        Assert.All(cancelled.Attempts, attempt => Assert.Equal(FailureCategory.TaskError, attempt.Failure));
        Assert.False(afterCancellation);
    }

    [Fact]
    public async Task HostPublishersReachTaskDelegatesAndPreCancelledRunDoesNotExecute()
    {
        var reports = new ReportPublisher();
        var artifacts = new ArtifactPublisher();
        var workflow = Workflow.Define("publish", "1", w => w.Type("test").Node("node", n =>
            n.Task("publish", async ctx =>
            {
                await ctx.Reports.PublishAsync("text", "Summary", new byte[] { 1 }, ctx.CancellationToken);
                using var content = new MemoryStream([2]);
                await ctx.Artifacts.PublishAsync("example.bin", content, "application/octet-stream", ctx.CancellationToken);
            })));
        Assert.Equal(0, (await new LocalEngine(workflow, output: TextWriter.Null, reports: reports, artifacts: artifacts)
            .RunAsync(Token)).ExitCode);
        Assert.Equal(1, reports.Calls);
        Assert.Equal(1, artifacts.Calls);
        var cancelled = await new LocalEngine(workflow, output: TextWriter.Null).RunAsync(new CancellationToken(true));
        Assert.Equal(RunStatus.Aborted, cancelled.Snapshot.Status);
        Assert.Empty(cancelled.Attempts);
    }

    [Fact]
    public async Task CancelledRunTokenAbortsButCleanupGetsFreshCancellationToken()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var started = Signal();
        var cleanup = false;
        var workflow = Workflow.Define("token", "1", w => w.Type("test").Node("body", n => n.Task("wait", async ctx =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ctx.CancellationToken);
        })).PostProcess(ctx => { ctx.CancellationToken.ThrowIfCancellationRequested(); cleanup = true; }));
        var execution = new LocalEngine(workflow, output: TextWriter.Null).RunAsync(cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        cancellation.Cancel();
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Equal(RunStatus.Aborted, result.Snapshot.Status);
        Assert.True(cleanup);
    }

    [Fact]
    public async Task UncooperativeFunctionFailsExplicitlyInsteadOfClaimingItStopped()
    {
        var clock = new ManualTimeProvider();
        var started = Signal();
        var cancelled = Signal();
        var release = Signal();
        var stopped = Signal();
        var workflow = Workflow.Define("uncooperative", "1", w => w.Type("test").Node("body", n =>
            n.Task("wait", async ctx =>
            {
                using var registration = ctx.CancellationToken.Register(() => cancelled.TrySetResult());
                started.TrySetResult();
                try { await release.Task; }
                finally { stopped.TrySetResult(); }
            }, p => p.Timeout(TimeSpan.FromMilliseconds(40)))));
        try
        {
            var execution = new LocalEngine(workflow, output: TextWriter.Null, timeProvider: clock,
                cancellationGrace: TimeSpan.FromMilliseconds(40)).RunAsync(Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            clock.Advance(TimeSpan.FromMilliseconds(40));
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            clock.Advance(TimeSpan.FromMilliseconds(40));
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                execution.WaitAsync(TimeSpan.FromSeconds(10), Token));
            Assert.Contains("cannot be forcibly killed", error.Message);
        }
        finally
        {
            release.TrySetResult();
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        }
    }

    [Fact]
    public async Task EmptyRunsAndNodesFinishAndAnEngineCannotRunTwice()
    {
        foreach (var withNode in new[] { false, true })
        {
            var workflow = Workflow.Define("empty", "1", w =>
            {
                w.Type("test");
                if (withNode)
                    w.Node("empty", _ => { });
            });
            var engine = new LocalEngine(workflow, output: TextWriter.Null);
            Assert.Equal(0, (await engine.RunAsync(Token)).ExitCode);
            await Assert.ThrowsAsync<InvalidOperationException>(() => engine.RunAsync(Token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => engine.AbortAsync(AbortRecord()));
        }
    }

    [Fact]
    public async Task InvalidPoliciesAndVariablesFailBeforeAnyTaskStarts()
    {
        var called = false;
        var invalid = Workflow.Define("invalid", "1", w => w.Type("test").Node("node", n =>
            n.Task("work", _ => { called = true; }, p => p.Retry(11))));
        Assert.Contains("node/work", Assert.Throws<ArgumentException>(() =>
            new LocalEngine(invalid, output: TextWriter.Null)).Message);
        var required = Workflow.Define("required", "1", w => w.Type("test").Variables(v => v.Int("count", required: true)));
        Assert.Throws<ArgumentException>(() => new LocalEngine(required, output: TextWriter.Null));
        var engine = new LocalEngine(Workflow.Define("valid", "1", w => w.Type("test")), output: TextWriter.Null);
        await Assert.ThrowsAsync<ArgumentException>(() => engine.AbortAsync(new("local", "Local", DateTimeOffset.UtcNow.AddHours(1),
            "future", AbortSource.Local)));
        Assert.False(called);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static AbortRecord AbortRecord(string reason = "Stop") =>
        new("local", "Local operator", DateTimeOffset.UtcNow, reason, AbortSource.Local);
    private static void AssertTerminalConservation(LocalRunResult result)
    {
        Assert.All(result.Snapshot.Tasks.Values, state => Assert.True(Wf.Engine.Core.StateMachine.TransitionTable.IsTerminal(state)));
        foreach (var key in result.Snapshot.Tasks.Keys)
            Assert.Single(result.Events, e => e.Entity == EventEntity.Task && e.Key == key &&
                Wf.Engine.Core.StateMachine.TransitionTable.IsTerminal((ExecutionStatus)e.State));
        Assert.Single(result.Events, e => e.Entity == EventEntity.Run &&
            Wf.Engine.Core.StateMachine.TransitionTable.IsTerminal((RunStatus)e.State));
        Assert.NotNull(result.Snapshot.EndedAt);
    }

    private sealed class SignalWriter(string expected) : TextWriter
    {
        public TaskCompletionSource Seen { get; } = Signal();
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
        public override void WriteLine(string? value)
        {
            if (value == expected)
                Seen.TrySetResult();
        }
    }

    private sealed class ReportPublisher : IReports
    {
        public int Calls { get; private set; }
        public Task PublishAsync(string kind, string title, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken,
            IReadOnlyList<string>? tags = null)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }

    private sealed class ArtifactPublisher : IArtifacts
    {
        public int Calls { get; private set; }
        public Task<Uri> PublishAsync(string name, Stream content, string contentType, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new Uri("file:///example.bin"));
        }
    }
}
