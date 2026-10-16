using System.Diagnostics;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;
using Wf.Agent.Hosts;
using Wf.Agent.Journal;
using Wf.Contracts.Agent.V1;
using Wf.TaskHost.Fixture;

namespace Wf.Agent.Tests;

public sealed class TaskHostPoolTests
{
    private static TaskHostPackage Package => new("1.0.0", "dotnet",
        ["exec", "--runtimeconfig", Path.ChangeExtension(typeof(TaskHostPoolTests).Assembly.Location, ".runtimeconfig.json"),
            "--depsfile", Path.ChangeExtension(typeof(TaskHostPoolTests).Assembly.Location, ".deps.json"), typeof(FixtureMarker).Assembly.Location],
        AppContext.BaseDirectory);

    private static TaskHostPool Pool() => new(NullLogger<TaskHostPool>.Instance);
    private static Dispatch Dispatch(string task = "echo", TimeSpan? timeout = null) => new()
    {
        Task = new AttemptRef { RunId = Guid.NewGuid().ToString(), NodeKey = "work", TaskKey = task, Attempt = 1 },
        Version = "1.0.0", DeadlineAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(20))),
        InputsJson = ByteString.CopyFromUtf8("""{"source":{"value":7}}"""),
        VariablesJson = ByteString.CopyFromUtf8("""{"amount":5}""")
    };

    [Fact]
    public async Task Warm_dispatch_executes_real_delegate_without_process_start_and_ack_exits()
    {
        await using var pool = Pool();
        await pool.PrewarmAsync(Package, TestContext.Current.CancellationToken);
        var pid = Assert.Single(pool.WarmProcessIds);
        Assert.Equal(1, pool.ProcessStartCount);
        var result = await pool.ExecuteAsync(Package, Dispatch(), ByteString.CopyFromUtf8("""{"previous":3}"""),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, pool.ProcessStartCount);
        Assert.Empty(pool.WarmProcessIds);
        Assert.Equal(TaskOutcome.Succeeded, result.Outcome);
        using var outputs = JsonDocument.Parse(result.OutputsJson.Memory);
        Assert.Equal(15, outputs.RootElement.GetProperty("result").GetInt32());
        Assert.Equal(pid, outputs.RootElement.GetProperty("pid").GetInt32());
        Assert.Equal("work/echo", outputs.RootElement.GetProperty("taskKey").GetString());
        AssertStopped(pid);
    }

    [Fact]
    public async Task Result_is_durable_before_host_acknowledgement_allows_process_exit()
    {
        var root = Path.GetFullPath(Path.Combine(".agent-artifacts", "host-journal-tests", Guid.NewGuid().ToString("N")));
        var id = Guid.NewGuid().ToString();
        var dispatch = Dispatch();
        try
        {
            await using var pool = Pool();
            await pool.PrewarmAsync(Package, TestContext.Current.CancellationToken);
            var pid = Assert.Single(pool.WarmProcessIds);
            using (var outbox = new AgentOutbox(new ResultJournal(root, id)))
            {
                var called = false;
                var result = await pool.ExecuteAsync(Package, dispatch, cancellationToken: TestContext.Current.CancellationToken,
                    journalResult: completed =>
                    {
                        using var host = Process.GetProcessById(pid);
                        Assert.False(host.HasExited);
                        outbox.Enqueue(new AgentMessage { TaskCompleted = completed });
                        called = true;
                    });
                Assert.True(called);
                Assert.Equal(result, outbox.NextAfter(0)!.TaskCompleted);
                AssertStopped(pid);
            }
            using var restored = new AgentOutbox(new ResultJournal(root, id));
            Assert.Equal(dispatch.Task, restored.NextAfter(0)!.TaskCompleted.Task);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Concurrent_attempts_have_distinct_warm_processes_and_cannot_share_memory()
    {
        await using var pool = Pool();
        await pool.PrewarmAsync(Package, TestContext.Current.CancellationToken);
        await pool.PrewarmAsync(Package, TestContext.Current.CancellationToken);
        var pids = pool.WarmProcessIds;
        var attempts = Enumerable.Range(0, 2).Select(_ => pool.ExecuteAsync(Package, Dispatch(),
            ByteString.CopyFromUtf8("""{"previous":0}"""), cancellationToken: TestContext.Current.CancellationToken)).ToArray();
        var results = await Task.WhenAll(attempts);
        Assert.Equal(2, pool.ProcessStartCount);
        var actual = results.Select(result =>
        {
            using var output = JsonDocument.Parse(result.OutputsJson.Memory);
            return output.RootElement.GetProperty("pid").GetInt32();
        }).ToArray();
        Assert.Equal(pids.Order(), actual.Order());
        Assert.NotEqual(actual[0], actual[1]);
        foreach (var pid in pids) AssertStopped(pid);
    }

    [Theory]
    [InlineData("fail", "TaskError", false)]
    [InlineData("nonretryable", "TaskError", true)]
    [InlineData("selfcancel", "TaskError", false)]
    public async Task User_failures_keep_classification_and_retry_semantics(string task, string category, bool nonRetryable)
    {
        await using var pool = Pool();
        var result = await pool.ExecuteAsync(Package, Dispatch(task), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(TaskOutcome.Failed, result.Outcome);
        Assert.Equal(category, result.Error.Category);
        Assert.Equal(nonRetryable, result.Error.NonRetryable);
        Assert.NotEmpty(result.Error.Message);
    }

    [Theory]
    [InlineData("TaskError")]
    [InlineData("Timeout")]
    [InlineData("Aborted")]
    [InlineData("AgentLost")]
    [InlineData("ResourceLimit")]
    [InlineData("ValidationError")]
    [InlineData("Placement")]
    [InlineData("PlatformError")]
    public async Task FailureClassifier_preserves_all_eight_scripted_categories_in_actual_task_hosts(string category)
    {
        await using var pool = Pool();
        var dispatch = Dispatch("classified");
        dispatch.InputsJson = ByteString.CopyFromUtf8(JsonSerializer.Serialize(new { source = new { value = category } }));
        var result = await pool.ExecuteAsync(Package, dispatch, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(TaskOutcome.Failed, result.Outcome);
        Assert.Equal(category, result.Error.Category);
        Assert.Equal(category == "Aborted", result.Error.NonRetryable);
    }

    [Theory]
    [InlineData("cancelerror", false, false)]
    [InlineData("cancelauthor", false, true)]
    [InlineData("cancelerror", true, false)]
    [InlineData("cancelauthor", true, true)]
    public async Task FailureClassifier_does_not_relabel_normal_errors_when_actual_cancellation_races(
        string task, bool timedOut, bool nonRetryable)
    {
        await using var pool = Pool();
        await pool.PrewarmAsync(Package, TestContext.Current.CancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        if (!timedOut) cancellation.CancelAfter(TimeSpan.FromMilliseconds(200));
        var result = await pool.ExecuteAsync(Package, Dispatch(task, timedOut ? TimeSpan.FromMilliseconds(200) : null),
            cancellationToken: cancellation.Token);
        Assert.Equal(timedOut ? TaskOutcome.TimedOut : TaskOutcome.Cancelled, result.Outcome);
        Assert.Equal("TaskError", result.Error.Category);
        Assert.Equal(nonRetryable, result.Error.NonRetryable);
    }

    [Theory]
    [InlineData("delay")]
    [InlineData("badcallback")]
    public async Task Cooperative_deadline_returns_timed_out_not_platform_error(string task)
    {
        await using var pool = Pool();
        await pool.PrewarmAsync(Package, TestContext.Current.CancellationToken);
        var result = await pool.ExecuteAsync(Package, Dispatch(task, TimeSpan.FromMilliseconds(200)),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(TaskOutcome.TimedOut, result.Outcome);
        Assert.Equal("Timeout", result.Error.Category);
        Assert.False(result.Error.NonRetryable);
    }

    [Fact]
    public async Task Uncooperative_attempt_is_force_killed_without_harming_sibling()
    {
        await using var pool = Pool();
        await pool.PrewarmAsync(Package, TestContext.Current.CancellationToken);
        await pool.PrewarmAsync(Package, TestContext.Current.CancellationToken);
        var blockedPid = pool.WarmProcessIds[0];
        var blocked = pool.ExecuteAsync(Package, Dispatch("block", TimeSpan.FromMilliseconds(200)),
            cancellationGrace: TimeSpan.FromMilliseconds(100), cancellationToken: TestContext.Current.CancellationToken);
        var sibling = pool.ExecuteAsync(Package, Dispatch(), ByteString.CopyFromUtf8("""{"previous":1}"""),
            cancellationToken: TestContext.Current.CancellationToken);
        var results = await Task.WhenAll(blocked, sibling);
        Assert.Equal(TaskOutcome.TimedOut, results[0].Outcome);
        Assert.Contains("terminated", results[0].Error.Message);
        Assert.Equal(TaskOutcome.Succeeded, results[1].Outcome);
        Assert.Equal(2, pool.ProcessStartCount);
        AssertStopped(blockedPid);
    }

    [Theory]
    [InlineData("delay")]
    [InlineData("badcallback")]
    public async Task Caller_cancellation_reaches_delegate_and_is_nonretryable(string task)
    {
        await using var pool = Pool();
        await pool.PrewarmAsync(Package, TestContext.Current.CancellationToken);
        var pid = Assert.Single(pool.WarmProcessIds);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var result = await pool.ExecuteAsync(Package, Dispatch(task), cancellationToken: cancellation.Token);
        Assert.Equal(TaskOutcome.Cancelled, result.Outcome);
        Assert.Equal("Aborted", result.Error.Category);
        Assert.True(result.Error.NonRetryable);
        AssertStopped(pid);
    }

    [Fact]
    public async Task Uds_directory_is_accessible_only_to_its_owner()
    {
        await using var pool = Pool();
        var result = await pool.ExecuteAsync(Package, Dispatch("permissions"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(TaskOutcome.Succeeded, result.Outcome);
        using var outputs = JsonDocument.Parse(result.OutputsJson.Memory);
        Assert.True(outputs.RootElement.GetProperty("private").GetBoolean());
    }

    [Fact]
    public async Task Forced_cancellation_terminates_user_child_processes()
    {
        await using var pool = Pool();
        await pool.PrewarmAsync(Package, TestContext.Current.CancellationToken);
        var marker = Path.GetTempFileName();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        try
        {
            var dispatch = Dispatch("child");
            dispatch.InputsJson = ByteString.CopyFromUtf8(JsonSerializer.Serialize(new { source = new { value = marker } }));
            var execution = pool.ExecuteAsync(Package, dispatch, cancellationGrace: TimeSpan.FromMilliseconds(100),
                cancellationToken: cancellation.Token);
            using var readiness = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            readiness.CancelAfter(TimeSpan.FromSeconds(10));
            while (new FileInfo(marker).Length == 0) await Task.Delay(10, readiness.Token);
            var childPid = int.Parse(await File.ReadAllTextAsync(marker, TestContext.Current.CancellationToken),
                System.Globalization.CultureInfo.InvariantCulture);
            await cancellation.CancelAsync();
            var result = await execution;
            Assert.Equal(TaskOutcome.Cancelled, result.Outcome);
            Assert.Contains("terminated", result.Error.Message);
            AssertStopped(childPid);
        }
        finally { File.Delete(marker); }
    }

    [Theory]
    [InlineData("""{"unknown":2}""", """{"source":{"value":7}}""")]
    [InlineData("""{"amount":"wrong"}""", """{"source":{"value":7}}""")]
    [InlineData("""{"amount":1,"amount":2}""", """{"source":{"value":7}}""")]
    [InlineData("[]", """{"source":{"value":7}}""")]
    [InlineData("{}", """{"source":7}""")]
    [InlineData("{}", """{"other":{"value":7}}""")]
    public async Task Invalid_dispatch_json_is_rejected_before_user_code(string variables, string inputs)
    {
        await using var pool = Pool();
        await pool.PrewarmAsync(Package, TestContext.Current.CancellationToken);
        var pid = Assert.Single(pool.WarmProcessIds);
        var dispatch = Dispatch();
        dispatch.VariablesJson = ByteString.CopyFromUtf8(variables);
        dispatch.InputsJson = ByteString.CopyFromUtf8(inputs);
        var error = await Assert.ThrowsAsync<Grpc.Core.RpcException>(() => pool.ExecuteAsync(Package, dispatch,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(Grpc.Core.StatusCode.InvalidArgument, error.StatusCode);
        AssertStopped(pid);
    }

    [Fact]
    public async Task Pre_cancelled_dispatch_does_not_consume_warm_host()
    {
        await using var pool = Pool();
        await pool.PrewarmAsync(Package, TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pool.ExecuteAsync(Package, Dispatch(), cancellationToken: cancellation.Token));
        Assert.Single(pool.WarmProcessIds);
        Assert.Equal(1, pool.ProcessStartCount);
    }

    [Fact]
    public async Task Readiness_checks_actual_workflow_version_and_cleans_failed_start()
    {
        await using var pool = Pool();
        var wrong = new TaskHostPackage("wrong", Package.Executable, Package.Arguments, Package.WorkingDirectory);
        await Assert.ThrowsAsync<InvalidDataException>(() => pool.PrewarmAsync(wrong, TestContext.Current.CancellationToken));
        Assert.Empty(pool.WarmProcessIds);
    }

    [Fact]
    public async Task Failed_executable_launch_surfaces_error_and_leaves_pool_usable()
    {
        await using var pool = Pool();
        var bad = new TaskHostPackage("1.0.0", Path.Combine(AppContext.BaseDirectory, "not-a-real-executable"), [], AppContext.BaseDirectory);
        await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() => pool.PrewarmAsync(bad, TestContext.Current.CancellationToken));
        await pool.PrewarmAsync(Package, TestContext.Current.CancellationToken);
        Assert.Single(pool.WarmProcessIds);
    }

    [Fact]
    public async Task Dispose_stops_warm_processes_and_rejects_new_work()
    {
        var pool = Pool();
        await pool.PrewarmAsync(Package, TestContext.Current.CancellationToken);
        var pid = Assert.Single(pool.WarmProcessIds);
        await pool.DisposeAsync();
        AssertStopped(pid);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => pool.PrewarmAsync(Package, TestContext.Current.CancellationToken));
        await pool.DisposeAsync();
    }

    [Fact]
    public async Task Warm_pool_evicts_oldest_before_starting_a_fifth_process()
    {
        await using var pool = Pool();
        for (var index = 0; index < 4; index++) await pool.PrewarmAsync(Package, TestContext.Current.CancellationToken);
        var oldest = pool.WarmProcessIds[0];
        await pool.PrewarmAsync(Package, TestContext.Current.CancellationToken);
        Assert.Equal(5, pool.ProcessStartCount);
        Assert.DoesNotContain(oldest, pool.WarmProcessIds);
        Assert.True(pool.WarmProcessIds.Count <= 4);
        AssertStopped(oldest);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(31000)]
    public async Task Invalid_grace_is_rejected_before_process_start(int milliseconds)
    {
        await using var pool = Pool();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => pool.ExecuteAsync(Package, Dispatch(),
            cancellationGrace: TimeSpan.FromMilliseconds(milliseconds), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(0, pool.ProcessStartCount);
    }

    [Fact]
    public void Package_snapshots_arguments_and_requires_absolute_working_directory()
    {
        string[] arguments = ["original"];
        var package = new TaskHostPackage("1", "dotnet", arguments, AppContext.BaseDirectory);
        arguments[0] = "changed";
        Assert.Equal("original", package.Arguments[0]);
        Assert.Throws<ArgumentException>(() => new TaskHostPackage("1", "dotnet", [], "relative"));
        Assert.Throws<ArgumentException>(() => new TaskHostPackage("", "dotnet", [], AppContext.BaseDirectory));
        Assert.Throws<ArgumentException>(() => new TaskHostPackage("1", "", [], AppContext.BaseDirectory));
    }

    private static void AssertStopped(int pid)
    {
        try { using var process = Process.GetProcessById(pid); Assert.True(process.HasExited); }
        catch (ArgumentException) { }
    }
}
