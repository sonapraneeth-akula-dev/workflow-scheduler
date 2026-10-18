using System.Net;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;
using Wf.Agent;
using Wf.Agent.Hosts;
using Wf.Agent.Journal;
using Wf.Contracts.Agent.V1;

namespace Wf.Agent.Tests;

public sealed class TaskCommandsTests
{
    [Theory]
    [InlineData(double.NaN, 0UL)]
    [InlineData(-1, 0UL)]
    [InlineData(0, ulong.MaxValue)]
    public async Task Invalid_resource_caps_cannot_download_packages_mutate_journal_or_start_tasks(double cpu, ulong ram)
    {
        await using var cache = new PackageCacheTests.CacheFixture([]);
        await using var hosts = new TaskHostPool(NullLogger<TaskHostPool>.Instance);
        using var outbox = new AgentOutbox();
        await using var commands = Create(cache, hosts, outbox);
        var message = Dispatch(cache.Sha);
        message.Dispatch.Limits = new() { CpuCores = cpu, RamMb = ram };
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await commands.HandleAsync(message, TestContext.Current.CancellationToken));
        Assert.Empty(commands.Inflight);
        Assert.Null(outbox.NextAfter(0));
        Assert.Equal(0, cache.Handler.Count);
        Assert.Equal(0, hosts.ProcessStartCount);
    }

    [Theory]
    [InlineData(false, CalibrationOutcome.Unsupported)]
    [InlineData(true, CalibrationOutcome.TimedOut)]
    public async Task Calibration_reports_explicit_terminal_failure_without_fabricating_load_or_capacity(
        bool expired, CalibrationOutcome expected)
    {
        await using var cache = new PackageCacheTests.CacheFixture([]);
        await using var hosts = new TaskHostPool(NullLogger<TaskHostPool>.Instance);
        using var outbox = new AgentOutbox();
        await using var commands = Create(cache, hosts, outbox);
        var message = Calibration(expired);
        await commands.HandleAsync(message, TestContext.Current.CancellationToken);
        await commands.HandleAsync(message.Clone(), TestContext.Current.CancellationToken);
        var result = outbox.NextAfter(0)!;
        Assert.Equal(expected, result.CalibrationCompleted.Outcome);
        Assert.Equal(0, result.CalibrationCompleted.CapacityRps);
        Assert.Equal(2UL, outbox.ReservationEpoch);
        Assert.Null(outbox.NextAfter(result.Sequence));
        Assert.Empty(commands.Inflight);
        Assert.Equal(0, hosts.ProcessStartCount);
        Assert.Equal(0, cache.Handler.Count);
    }

    [Fact]
    public async Task Calibration_cannot_interrupt_active_work_or_lower_the_machine_fence()
    {
        var began = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var cache = new PackageCacheTests.CacheFixture([], async (_, token) =>
        {
            began.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("unreachable");
        });
        await using var hosts = new TaskHostPool(NullLogger<TaskHostPool>.Instance);
        using var outbox = new AgentOutbox();
        await using var commands = Create(cache, hosts, outbox);
        await commands.HandleAsync(Dispatch(cache.Sha), TestContext.Current.CancellationToken);
        await began.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var calibration = Calibration(false);
        await commands.HandleAsync(calibration, TestContext.Current.CancellationToken);
        var result = outbox.NextAfter(outbox.NextAfter(0)!.Sequence)!;
        Assert.Equal(CalibrationOutcome.Busy, result.CalibrationCompleted.Outcome);
        Assert.Single(commands.Inflight);
        Assert.Equal(1UL, outbox.ReservationEpoch);
        await commands.DisposeAsync();
        outbox.ObserveReservationEpoch(4);
        await using var idle = Create(cache, hosts, outbox);
        var stale = Calibration(false);
        await idle.HandleAsync(stale, TestContext.Current.CancellationToken);
        Assert.False(outbox.ContainsCalibrationResult(stale.Calibrate.Id, stale.Calibrate.ReservationEpoch));
        Assert.Equal(4UL, outbox.ReservationEpoch);
    }

    [Fact]
    public async Task Calibration_result_and_fence_survive_restart_and_deduplicate_command_replay()
    {
        var root = Path.GetFullPath(Path.Combine(".agent-artifacts", "calibration-journal", Guid.NewGuid().ToString("N")));
        var id = Guid.NewGuid().ToString("D");
        try
        {
            await using var cache = new PackageCacheTests.CacheFixture([]);
            await using var hosts = new TaskHostPool(NullLogger<TaskHostPool>.Instance);
            var message = Calibration(false);
            ulong sequence;
            using (var original = new AgentOutbox(new ResultJournal(root, id)))
            {
                await using var commands = Create(cache, hosts, original);
                await commands.HandleAsync(message, TestContext.Current.CancellationToken);
                sequence = original.NextAfter(0)!.Sequence;
            }
            using var restored = new AgentOutbox(new ResultJournal(root, id));
            await using var restarted = Create(cache, hosts, restored);
            await restarted.HandleAsync(message, TestContext.Current.CancellationToken);
            Assert.Equal(2UL, restored.ReservationEpoch);
            Assert.Equal(sequence, restored.NextAfter(0)!.Sequence);
            Assert.Equal(CalibrationOutcome.Unsupported, restored.NextAfter(0)!.CalibrationCompleted.Outcome);
            Assert.Null(restored.NextAfter(sequence));
            restored.Acknowledge(new Ack { Sequence = sequence });
            Assert.Null(restored.NextAfter(0));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static ControlMessage Calibration(bool expired) => new()
    {
        Calibrate = new()
        {
            Id = Guid.CreateVersion7().ToString("D"), ReservationEpoch = 2,
            ProfileJson = new Wf.Contracts.CapacityProfile("http", "json", "small").ProfileKey,
            DeadlineAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(expired ? -1 : 1))
        }
    };

    [Fact]
    public async Task Dispatch_is_nonblocking_deduplicated_and_cancelled_while_downloading()
    {
        var began = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var cache = new PackageCacheTests.CacheFixture([], async (_, token) =>
        {
            began.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("unreachable");
        });
        await using var hosts = new TaskHostPool(NullLogger<TaskHostPool>.Instance);
        var outbox = new AgentOutbox();
        await using var commands = Create(cache, hosts, outbox);
        var dispatch = Dispatch(cache.Sha);
        await commands.HandleAsync(dispatch, TestContext.Current.CancellationToken);
        await began.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Single(commands.Inflight);
        await commands.HandleAsync(dispatch.Clone(), TestContext.Current.CancellationToken);
        Assert.Equal(1, cache.Handler.Count);
        var other = dispatch.Clone();
        other.Dispatch.Task.RunId = Guid.NewGuid().ToString();
        await Assert.ThrowsAsync<InvalidDataException>(async () => await commands.HandleAsync(other, TestContext.Current.CancellationToken));
        await commands.HandleAsync(new ControlMessage { Cancel = new Cancel { Task = dispatch.Dispatch.Task.Clone() } },
            TestContext.Current.CancellationToken);
        var completed = await Completion(outbox);
        Assert.Equal(TaskOutcome.Cancelled, completed.Outcome);
        Assert.Equal("Aborted", completed.Error.Category);
        Assert.True(completed.Error.NonRetryable);
        await commands.DisposeAsync();
        Assert.Empty(commands.Inflight);
        Assert.Equal(0, hosts.ProcessStartCount);
    }

    [Fact]
    public async Task Package_failure_is_reported_explicitly_once_and_does_not_start_a_host()
    {
        await using var cache = new PackageCacheTests.CacheFixture([], (_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        await using var hosts = new TaskHostPool(NullLogger<TaskHostPool>.Instance);
        var outbox = new AgentOutbox();
        await using var commands = Create(cache, hosts, outbox);
        var dispatch = Dispatch(cache.Sha);
        await commands.HandleAsync(dispatch, TestContext.Current.CancellationToken);
        var result = await Completion(outbox);
        Assert.Equal(TaskOutcome.Failed, result.Outcome);
        Assert.Equal("PlatformError", result.Error.Category);
        Assert.Contains("404", result.Error.Message);
        Assert.False(result.Error.NonRetryable);
        await commands.HandleAsync(dispatch, TestContext.Current.CancellationToken);
        Assert.Equal(1, cache.Handler.Count);
        Assert.Equal(0, hosts.ProcessStartCount);
    }

    [Fact]
    public async Task Persisted_completion_suppresses_redispatch_after_new_journal_and_command_objects()
    {
        var root = Path.GetFullPath(Path.Combine(".agent-artifacts", "command-journal-tests", Guid.NewGuid().ToString("N")));
        var id = Guid.NewGuid().ToString();
        try
        {
            await using var cache = new PackageCacheTests.CacheFixture([]);
            await using var hosts = new TaskHostPool(NullLogger<TaskHostPool>.Instance);
            var dispatch = Dispatch(cache.Sha);
            using (var original = new AgentOutbox(new ResultJournal(root, id)))
                original.Enqueue(new AgentMessage { TaskCompleted = new TaskCompleted
                {
                    Task = dispatch.Dispatch.Task.Clone(), Outcome = TaskOutcome.Succeeded,
                    At = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow)
                } });
            using var restored = new AgentOutbox(new ResultJournal(root, id));
            await using var commands = Create(cache, hosts, restored);
            await commands.HandleAsync(dispatch, TestContext.Current.CancellationToken);
            Assert.Empty(commands.Inflight);
            Assert.Equal(0, cache.Handler.Count);
            Assert.Equal(0, hosts.ProcessStartCount);
            Assert.NotNull(restored.NextAfter(0)!.TaskCompleted);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("deadline")]
    [InlineData("sha")]
    [InlineData("reservation")]
    public async Task Malformed_dispatch_cannot_start_or_enter_the_outbox(string field)
    {
        await using var cache = new PackageCacheTests.CacheFixture([]);
        await using var hosts = new TaskHostPool(NullLogger<TaskHostPool>.Instance);
        var outbox = new AgentOutbox();
        await using var commands = Create(cache, hosts, outbox);
        var message = Dispatch(cache.Sha);
        if (field == "identity") message.Dispatch.Task.RunId = "invalid";
        if (field == "deadline") message.Dispatch.DeadlineAt = null;
        if (field == "sha") message.Dispatch.Sha256 = "invalid";
        if (field == "reservation") message.Dispatch.ReservationEpoch = 0;
        await Assert.ThrowsAsync<InvalidDataException>(async () => await commands.HandleAsync(message, TestContext.Current.CancellationToken));
        Assert.Empty(commands.Inflight);
        Assert.Null(outbox.NextAfter(0));
        Assert.Equal(0, cache.Handler.Count);
    }

    [Theory]
    [InlineData("reports", "https://unconfigured.example/api/v1/report-publishes/token")]
    [InlineData("reports", "//unconfigured.example/api/v1/report-publishes/token")]
    [InlineData("reports", "http://localhost@unconfigured.example/api/v1/report-publishes/token")]
    [InlineData("reports", "http://localhost/api/v1/report-publishes/token?other=1")]
    [InlineData("reports", "http://localhost/api/v1/report-publishes/token#fragment")]
    [InlineData("reports", "http://localhost/api/v1/other/token")]
    [InlineData("reports", "duplicate")]
    [InlineData("artifacts", "https://unconfigured.example/api/v1/artifact-grants")]
    [InlineData("artifacts", "//unconfigured.example/api/v1/artifact-grants")]
    [InlineData("artifacts", "http://localhost@unconfigured.example/api/v1/artifact-grants")]
    [InlineData("artifacts", "http://localhost/api/v1/artifact-grants?other=1")]
    [InlineData("artifacts", "http://localhost/api/v1/artifact-grants#fragment")]
    [InlineData("artifacts", "http://localhost/api/v1/report-publishes")]
    [InlineData("artifacts", "duplicate")]
    [InlineData("checkpoints", "https://unconfigured.example/api/v1/checkpoint-uploads")]
    [InlineData("checkpoints", "//unconfigured.example/api/v1/checkpoint-uploads")]
    [InlineData("checkpoints", "http://localhost@unconfigured.example/api/v1/checkpoint-uploads")]
    [InlineData("checkpoints", "http://localhost/api/v1/checkpoint-uploads?other=1")]
    [InlineData("checkpoints", "http://localhost/api/v1/checkpoint-uploads#fragment")]
    [InlineData("checkpoints", "http://localhost/api/v1/report-publishes")]
    [InlineData("checkpoints", "duplicate")]
    public async Task ReportPublish_dispatch_capabilities_cannot_redirect_to_unconfigured_origins_or_ambiguous_targets(string kind, string uri)
    {
        await using var cache = new PackageCacheTests.CacheFixture([]);
        await using var hosts = new TaskHostPool(NullLogger<TaskHostPool>.Instance);
        using var outbox = new AgentOutbox();
        await using var commands = Create(cache, hosts, outbox);
        var message = Dispatch(cache.Sha);
        message.Dispatch.Grants.Add(new UploadGrant
        {
            Kind = kind, UploadUri = uri == "duplicate"
                ? kind switch
                {
                    "reports" => "/api/v1/report-publishes",
                    "artifacts" => "/api/v1/artifact-grants",
                    _ => "/api/v1/checkpoint-uploads"
                } : uri
        });
        if (uri == "duplicate")
            message.Dispatch.Grants.Add(message.Dispatch.Grants[0].Clone());
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await commands.HandleAsync(message, TestContext.Current.CancellationToken));
        Assert.Empty(commands.Inflight);
        Assert.Null(outbox.NextAfter(0));
        Assert.Equal(0, cache.Handler.Count);
        Assert.Equal(0, hosts.ProcessStartCount);
    }

    private static TaskCommands Create(PackageCacheTests.CacheFixture cache, TaskHostPool hosts, AgentOutbox outbox) =>
        new(new AgentOptions([new Uri("http://localhost")], Guid.NewGuid().ToString(), "test", "ci", 1),
            cache.Cache, hosts, outbox, TimeProvider.System, NullLogger<TaskCommands>.Instance);

    private static ControlMessage Dispatch(string sha) => new()
    {
        Dispatch = new Dispatch
        {
            Task = new AttemptRef { RunId = Guid.NewGuid().ToString(), NodeKey = "work", TaskKey = "echo", Attempt = 1 },
            Version = "1", DeadlineAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(30)),
            PackageUri = PackageCacheTests.CacheFixture.Url.ToString(), Sha256 = sha, ReservationEpoch = 1
        }
    };

    private static async Task<TaskCompleted> Completion(AgentOutbox outbox)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        ulong sequence = 0;
        while (true)
        {
            while (outbox.NextAfter(sequence) is { } message)
            {
                sequence = message.Sequence;
                if (message.TaskCompleted is not null) return message.TaskCompleted;
            }
            await outbox.WaitForChangeAsync(timeout.Token);
        }
    }
}
