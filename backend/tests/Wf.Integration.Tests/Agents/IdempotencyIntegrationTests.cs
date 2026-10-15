using System.Collections.Immutable;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;
using Wf.Contracts;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Definition;
using Wf.Contracts.Events;
using Wf.ControlPlane.Engine.Agents;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Sweeper;
using Wf.Engine.Core;
using Wf.Engine.Core.Placement;
using DispatchCommand = Wf.Contracts.Agent.V1.Dispatch;

namespace Wf.Integration.Tests.Agents;

public sealed class IdempotencyIntegrationTests(DispatchDatabase database) : IClassFixture<DispatchDatabase>
{
    private static readonly ImmutableArray<NodeDefinition> Nodes =
    [
        new("left", [], [new("shared"), new("other")]),
        new("right", [], [new("shared")])
    ];
    private readonly DateTimeOffset now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private RunStore Store => new(database.DataSource, NullLogger<RunStore>.Instance);

    [Theory]
    [InlineData(TaskOutcome.Succeeded, ExecutionStatus.Succeeded)]
    [InlineData(TaskOutcome.Failed, ExecutionStatus.Failed)]
    [InlineData(TaskOutcome.Cancelled, ExecutionStatus.Cancelled)]
    [InlineData(TaskOutcome.TimedOut, ExecutionStatus.TimedOut)]
    public async Task Replays_after_store_recreation_have_no_second_event_snapshot_attempt_or_output_update(
        TaskOutcome outcome, ExecutionStatus expected)
    {
        using var registry = new AgentRegistry(database.DataSource, TimeProvider.System);
        var session = await registry.RegisterAsync(Hello(), Token);
        var run = await CreateRun();
        using var dispatcher = Dispatcher(registry);
        var reference = await Dispatch(dispatcher, run);
        var started = Started(reference);
        await Store.AcceptAsync(session, started, Token);
        var running = await Evidence(run);
        await Store.AcceptAsync(session, started.Clone(), Token);
        Assert.Equal(running, await Evidence(run));

        var completed = Completed(reference, outcome);
        await Store.AcceptAsync(session, completed, Token);
        var terminal = await Evidence(run);
        var rebuilt = Store;
        var replay = completed.Clone();
        replay.Sequence = 100;
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => rebuilt.AcceptAsync(session, replay.Clone(), Token)));
        await rebuilt.AcceptAsync(session, started, Token);
        Assert.Equal(terminal, await Evidence(run));
        Assert.Equal(expected, (await rebuilt.LoadAsync(run.Snapshot.RunId, Token)).Snapshot.Tasks["left/shared"]);
        Assert.Equal(new[] { 1, 2, 3, (int)expected }, await States(reference));
        Assert.Empty(await rebuilt.PendingAsync(session, Token));
        await AssertOutputs(run, "left", outcome == TaskOutcome.Succeeded ? """{"value":7}""" : "{}");
    }

    [Fact]
    public async Task Success_before_started_is_rejected_without_acknowledging_dispatch_then_can_be_redelivered()
    {
        using var registry = new AgentRegistry(database.DataSource, TimeProvider.System);
        var session = await registry.RegisterAsync(Hello(), Token);
        var run = await CreateRun();
        using var dispatcher = Dispatcher(registry);
        var reference = await Dispatch(dispatcher, run);
        var dispatched = await Evidence(run);
        var completed = Completed(reference);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store.AcceptAsync(session, completed, Token));
        Assert.Equal(dispatched, await Evidence(run));
        Assert.Single(await Store.PendingAsync(session, Token));

        await Store.AcceptAsync(session, Started(reference), Token);
        await Store.AcceptAsync(session, completed, Token);
        Assert.Equal(new[] { 1, 2, 3, 5 }, await States(reference));
        await AssertOutputs(run, "left", """{"value":7}""");
    }

    [Fact]
    public async Task Interleaved_results_for_other_runs_nodes_and_tasks_never_share_a_deduplication_identity()
    {
        using var registry = new AgentRegistry(database.DataSource, TimeProvider.System);
        var session = await registry.RegisterAsync(Hello(), Token);
        var first = await CreateRun();
        var second = await CreateRun();
        using var dispatcher = Dispatcher(registry);
        var references = new[]
        {
            await Dispatch(dispatcher, first),
            await Dispatch(dispatcher, first, "right"),
            await Dispatch(dispatcher, first, task: "other"),
            await Dispatch(dispatcher, second)
        };
        foreach (var index in new[] { 2, 0, 3, 1 })
            await Store.AcceptAsync(session, Started(references[index]), Token);
        foreach (var index in new[] { 1, 3, 0, 2 })
        {
            var completed = Completed(references[index], outputs: $$"""{"{{references[index].TaskKey}}":{{index}}}""");
            await Store.AcceptAsync(session, completed, Token);
            var firstBefore = await Evidence(first);
            var secondBefore = await Evidence(second);
            await Store.AcceptAsync(session, completed.Clone(), Token);
            await Store.AcceptAsync(session, Started(references[index]), Token);
            Assert.Equal(firstBefore, await Evidence(first));
            Assert.Equal(secondBefore, await Evidence(second));
        }
        foreach (var reference in references)
        {
            Assert.Equal(new[] { 1, 2, 3, 5 }, await States(reference));
            Assert.Equal(ExecutionStatus.Succeeded,
                (await Store.LoadAsync(Guid.Parse(reference.RunId), Token)).Snapshot.Tasks[$"{reference.NodeKey}/{reference.TaskKey}"]);
        }
        await AssertOutputs(first, "left", """{"shared":0,"other":2}""");
        await AssertOutputs(first, "right", """{"shared":1}""");
        await AssertOutputs(second, "left", """{"shared":3}""");
    }

    [Theory]
    [InlineData("run")]
    [InlineData("node")]
    [InlineData("task")]
    [InlineData("attempt")]
    public async Task Unassigned_identity_is_a_conflict_not_a_replay_of_another_completed_attempt(string difference)
    {
        using var registry = new AgentRegistry(database.DataSource, TimeProvider.System);
        var session = await registry.RegisterAsync(Hello(), Token);
        var run = await CreateRun();
        var otherRun = await CreateRun();
        using var dispatcher = Dispatcher(registry);
        var reference = await Dispatch(dispatcher, run);
        await Store.AcceptAsync(session, Started(reference), Token);
        await Store.AcceptAsync(session, Completed(reference), Token);
        var conflicting = reference.Clone();
        switch (difference)
        {
            case "run": conflicting.RunId = otherRun.Snapshot.RunId.ToString("D"); break;
            case "node": conflicting.NodeKey = "right"; break;
            case "task": conflicting.TaskKey = "other"; break;
            case "attempt": conflicting.Attempt++; break;
        }
        var before = await Evidence(run);
        var otherBefore = await Evidence(otherRun);
        await Assert.ThrowsAsync<ArgumentException>(() => Store.AcceptAsync(session, Started(conflicting), Token));
        await Assert.ThrowsAsync<ArgumentException>(() => Store.AcceptAsync(session, Completed(conflicting), Token));
        Assert.Equal(before, await Evidence(run));
        Assert.Equal(otherBefore, await Evidence(otherRun));
    }

    [Theory]
    [InlineData("outcome")]
    [InlineData("outputs")]
    [InlineData("timestamp")]
    [InlineData("identity")]
    public async Task Malformed_duplicate_is_rejected_before_the_terminal_attempt_short_circuit(string invalid)
    {
        using var registry = new AgentRegistry(database.DataSource, TimeProvider.System);
        var session = await registry.RegisterAsync(Hello(), Token);
        var run = await CreateRun();
        using var dispatcher = Dispatcher(registry);
        var reference = await Dispatch(dispatcher, run);
        await Store.AcceptAsync(session, Started(reference), Token);
        var completed = Completed(reference);
        await Store.AcceptAsync(session, completed, Token);
        var before = await Evidence(run);
        var malformed = completed.Clone();
        switch (invalid)
        {
            case "outcome": malformed.TaskCompleted.Outcome = TaskOutcome.Unspecified; break;
            case "outputs": malformed.TaskCompleted.OutputsJson = ByteString.CopyFromUtf8("[]"); break;
            case "timestamp": malformed.TaskCompleted.At = null; break;
            case "identity": malformed.TaskCompleted.Task.Attempt = 0; break;
        }
        await Assert.ThrowsAsync<ArgumentException>(() => Store.AcceptAsync(session, malformed, Token));
        Assert.Equal(before, await Evidence(run));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Superseded_and_foreign_sessions_are_rejected_even_when_the_payload_is_a_valid_replay(bool completed)
    {
        using var registry = new AgentRegistry(database.DataSource, TimeProvider.System);
        var hello = Hello();
        var old = await registry.RegisterAsync(hello, Token);
        var run = await CreateRun();
        using var dispatcher = Dispatcher(registry);
        var reference = await Dispatch(dispatcher, run);
        var started = Started(reference);
        var result = Completed(reference);
        await Store.AcceptAsync(old, started, Token);
        if (completed) await Store.AcceptAsync(old, result, Token);
        var current = await registry.RegisterAsync(hello, Token);
        Assert.NotEqual(old.SessionId, current.SessionId);
        var foreign = await registry.RegisterAsync(Hello(), Token);
        var before = await Evidence(run);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store.AcceptAsync(old, started, Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store.AcceptAsync(old, result, Token));
        await Assert.ThrowsAsync<ArgumentException>(() => Store.AcceptAsync(foreign, started, Token));
        await Assert.ThrowsAsync<ArgumentException>(() => Store.AcceptAsync(foreign, result, Token));
        Assert.Equal(before, await Evidence(run));

        await Store.AcceptAsync(current, started, Token);
        await Store.AcceptAsync(current, result, Token);
        var terminal = await Evidence(run);
        await Store.AcceptAsync(current, result.Clone(), Token);
        Assert.Equal(terminal, await Evidence(run));
        Assert.Equal(new[] { 1, 2, 3, 5 }, await States(reference));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task New_results_after_run_timeout_fail_without_mutation_but_an_existing_started_replay_is_harmless(bool started)
    {
        using var registry = new AgentRegistry(database.DataSource, TimeProvider.System);
        var session = await registry.RegisterAsync(Hello(), Token);
        var run = await CreateRun();
        using var dispatcher = Dispatcher(registry);
        var reference = await Dispatch(dispatcher, run);
        if (started) await Store.AcceptAsync(session, Started(reference), Token);
        run = await Store.LoadAsync(run.Snapshot.RunId, Token);
        await Store.AppendAsync(run, EventEntity.Run, null, (int)RunStatus.TimedOut, now.AddSeconds(1), Token);
        var before = await Evidence(run);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store.AcceptAsync(session, Completed(reference), Token));
        if (started)
            await Store.AcceptAsync(session, Started(reference), Token);
        else
            await Assert.ThrowsAsync<InvalidOperationException>(() => Store.AcceptAsync(session, Started(reference), Token));
        Assert.Equal(before, await Evidence(run));
        await AssertOutputs(run, "left", "{}");
    }

    [Fact]
    public async Task A_committed_completion_can_be_replayed_after_its_run_becomes_terminal()
    {
        using var registry = new AgentRegistry(database.DataSource, TimeProvider.System);
        var session = await registry.RegisterAsync(Hello(), Token);
        var run = await CreateRun();
        using var dispatcher = Dispatcher(registry);
        var reference = await Dispatch(dispatcher, run);
        await Store.AcceptAsync(session, Started(reference), Token);
        var completed = Completed(reference);
        await Store.AcceptAsync(session, completed, Token);
        run = await Store.LoadAsync(run.Snapshot.RunId, Token);
        await Store.AppendAsync(run, EventEntity.Run, null, (int)RunStatus.Succeeded, now.AddSeconds(1), Token);
        var before = await Evidence(run);
        await Store.AcceptAsync(session, completed.Clone(), Token);
        await Store.AcceptAsync(session, Started(reference), Token);
        Assert.Equal(before, await Evidence(run));
        Assert.Equal(new[] { 1, 2, 3, 5 }, await States(reference));
    }

    [Theory]
    [InlineData("dispatched")]
    [InlineData("running")]
    [InlineData("completed")]
    public async Task Delayed_old_attempt_messages_cannot_change_a_replacement_in_any_delivery_order(string phase)
    {
        using var registry = new AgentRegistry(database.DataSource, TimeProvider.System);
        var oldSession = await registry.RegisterAsync(Hello(), Token);
        var run = await CreateRun();
        using var dispatcher = Dispatcher(registry);
        var old = await Dispatch(dispatcher, run);
        await Store.AcceptAsync(oldSession, Started(old), Token);
        using var sweeper = new AgentLossSweeper(database.DataSource, registry, TimeProvider.System,
            NullLogger<AgentLossSweeper>.Instance);
        Assert.Single(await sweeper.ReconcileAsync(oldSession, [], Token));
        var replacementSession = await registry.RegisterAsync(Hello(), Token);
        Assert.Equal(AgentLossDisposition.Replaced,
            await dispatcher.ReplaceLostAsync(run.Snapshot.RunId, "left/shared", 1, Token));
        var replacement = Assert.Single(await Store.PendingAsync(replacementSession, Token)).Command.Task;
        Assert.Equal(2U, replacement.Attempt);
        if (phase != "dispatched") await Store.AcceptAsync(replacementSession, Started(replacement), Token);
        var result = Completed(replacement);
        if (phase == "completed") await Store.AcceptAsync(replacementSession, result, Token);
        var before = await Evidence(run);

        await Store.AcceptAsync(oldSession, Completed(old, outputs: """{"stale":true,"value":-1}"""), Token);
        await Store.AcceptAsync(oldSession, Started(old), Token);
        Assert.Equal(before, await Evidence(run));
        if (phase == "dispatched")
        {
            Assert.Single(await Store.PendingAsync(replacementSession, Token));
            await Store.AcceptAsync(replacementSession, Started(replacement), Token);
        }
        if (phase != "completed") await Store.AcceptAsync(replacementSession, result, Token);
        var terminal = await Evidence(run);
        await Store.AcceptAsync(oldSession, Started(old), Token);
        await Store.AcceptAsync(oldSession, Completed(old), Token);
        await Store.AcceptAsync(replacementSession, result.Clone(), Token);
        Assert.Equal(terminal, await Evidence(run));
        Assert.Equal(ExecutionStatus.Succeeded, (await Store.LoadAsync(run.Snapshot.RunId, Token)).Snapshot.Tasks["left/shared"]);
        Assert.Equal(new[] { 1, 2, 3, 4, 2, 3, 5 }, await States(replacement));
        await AssertOutputs(run, "left", """{"value":7}""");
    }

    private async Task<RunState> CreateRun()
    {
        var run = new RunState(Guid.CreateVersion7(), Nodes,
            new WorkflowMetadata("idempotency", ImmutableSortedDictionary<string, string>.Empty), Assert.Fail);
        await Store.CreateAsync(run, "tests/idempotency", Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours(1), Token);
        await Store.AppendAsync(run, EventEntity.Run, null, (int)RunStatus.Running, now, Token);
        return run;
    }

    private Dispatcher Dispatcher(AgentRegistry registry) =>
        new(database.DataSource, Store, registry, new PlacementEngine(), TimeProvider.System);

    private async Task<AttemptRef> Dispatch(Dispatcher dispatcher, RunState run, string node = "left", string task = "shared")
    {
        await Store.AppendAsync(run, EventEntity.Task, $"{node}/{task}", (int)ExecutionStatus.Ready, now, Token);
        var command = new DispatchCommand
        {
            Task = new AttemptRef { RunId = run.Snapshot.RunId.ToString("D"), NodeKey = node, TaskKey = task, Attempt = 1 },
            Version = "1.0.0", PackageUri = "https://packages.invalid/idempotency.zip", Sha256 = new string('a', 64),
            DeadlineAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(10))
        };
        Assert.NotNull((await dispatcher.DispatchAsync(run, new(run.Snapshot.RunId, node, task, 1, command.Sha256),
            command, Token)).Agent);
        return command.Task.Clone();
    }

    private AgentMessage Started(AttemptRef reference) => new()
    {
        Sequence = 1, TaskStarted = new TaskStarted { Task = reference.Clone(), At = Timestamp.FromDateTimeOffset(now) }
    };

    private AgentMessage Completed(AttemptRef reference, TaskOutcome outcome = TaskOutcome.Succeeded,
        string outputs = """{"value":7}""") => new()
    {
        Sequence = 2, TaskCompleted = new TaskCompleted
        {
            Task = reference.Clone(), At = Timestamp.FromDateTimeOffset(now.AddSeconds(1)), Outcome = outcome,
            OutputsJson = ByteString.CopyFromUtf8(outputs),
            Error = outcome == TaskOutcome.Succeeded ? null : new TaskError { Message = "original failure" }
        }
    };

    private static Hello Hello()
    {
        var id = Guid.NewGuid();
        return new Hello
        {
            Id = id.ToString("D"), Name = $"idempotency-{id:N}", Os = "linux", Arch = "x64", Cores = 4,
            RamMb = 8192, MaxTasks = 8, Pool = "test", Version = "1.0", ProtocolVersion = 1
        };
    }

    private async Task<int[]> States(AttemptRef reference)
    {
        await using var command = database.DataSource.CreateCommand("""
            SELECT payload FROM run_events WHERE run_id=$1 AND entity_key=$2 ORDER BY seq
            """);
        command.Parameters.AddWithValue(Guid.Parse(reference.RunId));
        command.Parameters.AddWithValue($"{reference.NodeKey}/{reference.TaskKey}");
        await using var reader = await command.ExecuteReaderAsync(Token);
        var states = new List<int>();
        while (await reader.ReadAsync(Token))
        {
            using var document = JsonDocument.Parse(reader.GetFieldValue<byte[]>(0));
            states.Add(document.RootElement.GetProperty("State").GetInt32());
        }
        return states.ToArray();
    }

    private async Task AssertOutputs(RunState run, string node, string expected)
    {
        await using var command = database.DataSource.CreateCommand("""
            SELECT COALESCE(outputs,'{}'::jsonb)=$3::jsonb FROM node_runs WHERE run_id=$1 AND node_key=$2
            """);
        command.Parameters.AddWithValue(run.Snapshot.RunId);
        command.Parameters.AddWithValue(node);
        command.Parameters.AddWithValue(expected);
        Assert.Equal(true, await command.ExecuteScalarAsync(Token));
    }

    private async Task<string> Evidence(RunState run)
    {
        // xmin detects a repeated outputs UPDATE even when the JSON value stays identical.
        await using var command = database.DataSource.CreateCommand("""
            SELECT jsonb_build_object(
                'run', (SELECT to_jsonb(r) FROM runs r WHERE r.id=$1),
                'events', (SELECT jsonb_agg(to_jsonb(e) ORDER BY e.seq) FROM run_events e WHERE e.run_id=$1),
                'snapshots', (SELECT jsonb_agg(s.seq ORDER BY s.seq) FROM run_snapshots s WHERE s.run_id=$1),
                'attempts', (SELECT jsonb_agg(to_jsonb(a) ORDER BY a.task_key,a.attempt) FROM task_attempts a WHERE a.run_id=$1),
                'nodes', (SELECT jsonb_agg(to_jsonb(n) || jsonb_build_object('version',n.xmin::text) ORDER BY n.node_key)
                    FROM node_runs n WHERE n.run_id=$1),
                'outbox', (SELECT jsonb_agg(to_jsonb(o) ORDER BY o.id) FROM dispatch_outbox o WHERE o.run_id=$1)
            )::text
            """);
        command.Parameters.AddWithValue(run.Snapshot.RunId);
        return Assert.IsType<string>(await command.ExecuteScalarAsync(Token));
    }
}
