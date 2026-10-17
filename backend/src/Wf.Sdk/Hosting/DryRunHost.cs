using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Contracts.Events;
using Wf.Contracts.Checkpoints;
using Wf.Engine.Core;
using Wf.Sdk.Local;

namespace Wf.Sdk.Hosting;

public enum MockOutcome { Succeed, Fail, Timeout, Throw, FailThenSucceed, ExecuteCheckpointed }
public sealed record CheckpointMockData(JsonElement State, long Sequence, DateTimeOffset SavedAt, uint Attempt = 2);
public sealed record TaskMock(MockOutcome Outcome = MockOutcome.Succeed,
    ImmutableDictionary<string, JsonElement>? Outputs = null, int FailuresBeforeSuccess = 1,
    string ErrorMessage = "Mock task failure.", FailureCategory Category = FailureCategory.TaskError, bool Retryable = true,
    CheckpointMockData? Checkpoint = null);
public sealed record DryRunSpec(ImmutableDictionary<string, TaskMock>? Tasks = null, TaskMock? Default = null,
    ImmutableDictionary<string, JsonElement>? Variables = null);
public sealed record DryRunAttempt(string TaskKey, int Attempt, DateTimeOffset StartedAt, DateTimeOffset EndedAt,
    ExecutionStatus Status, FailureCategory? Failure, string? ErrorType, string? ErrorMessage);
public sealed record DryRunLog(RunSnapshot Snapshot, ImmutableArray<RunEvent> Events,
    ImmutableArray<DryRunAttempt> Attempts, ImmutableDictionary<string, ImmutableDictionary<string, JsonElement>> Outputs)
{
    public int ExitCode => Snapshot.Status == RunStatus.Succeeded ? 0 : 1;
}

public static class DryRunHost
{
    public static async Task<int> RunAsync(WorkflowDefinition workflow, string[] args, TextWriter? output = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length < 2 || args[0] != "--dry-run" || string.IsNullOrWhiteSpace(args[1]) || args[1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException("Dry-run mode requires --dry-run <mocks.json> followed by optional --var key=value arguments.", nameof(args));
        await using var file = File.OpenRead(args[1]);
        var spec = await JsonSerializer.DeserializeAsync(file, DryRunJsonContext.Default.DryRunSpec, cancellationToken)
            .ConfigureAwait(false) ?? throw new JsonException("A dry-run mock specification is required.");
        var overrides = LocalVariables.Parse(workflow.Variables, args[2..]);
        var log = await ExecuteAsync(workflow, spec with
        {
            Variables = (spec.Variables ?? ImmutableDictionary<string, JsonElement>.Empty).SetItems(overrides)
        }, cancellationToken).ConfigureAwait(false);
        await (output ?? Console.Out).WriteLineAsync(JsonSerializer.Serialize(log, DryRunJsonContext.Default.DryRunLog))
            .ConfigureAwait(false);
        return log.ExitCode;
    }

    public static async Task<DryRunLog> ExecuteAsync(WorkflowDefinition workflow, DryRunSpec spec,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(spec);
        var specified = spec.Tasks ?? ImmutableDictionary<string, TaskMock>.Empty;
        foreach (var key in specified.Keys)
            if (!workflow.Tasks.ContainsKey(key))
                throw new ArgumentException($"Mock task '{key}' is not defined by the workflow.", nameof(spec));
        if (spec.Default is { } defaultMock)
        {
            ValidateMock("default", defaultMock);
            if (defaultMock.Outcome == MockOutcome.ExecuteCheckpointed)
                throw new ArgumentException("Checkpoint execution requires an explicit task key, never a default mock.", nameof(spec));
        }
        var handlers = ImmutableDictionary.CreateBuilder<string, Func<TaskContext, Task>>(StringComparer.Ordinal);
        foreach (var key in workflow.Tasks.Keys)
        {
            var mock = specified.TryGetValue(key, out var declared) ? declared : spec.Default ??
                throw new ArgumentException($"Task '{key}' requires a mock or an explicit default mock.", nameof(spec));
            ValidateMock(key, mock);
            var copied = mock with
            {
                Outputs = mock.Outputs?.ToImmutableDictionary(p => p.Key, p => p.Value.Clone(), StringComparer.Ordinal),
                Checkpoint = mock.Checkpoint is { } seed ? seed with { State = seed.State.Clone() } : null
            };
            var attempt = 0;
            handlers.Add(key, context =>
            {
                var number = ++attempt;
                if (copied.Checkpoint is { } seed)
                {
                    var checkpoint = new CheckpointStore(initialState:
                        JsonSerializer.SerializeToUtf8Bytes(seed.State, OutputJsonContext.Default.JsonElement),
                        sequence: seed.Sequence, savedAt: seed.SavedAt);
                    return workflow.Tasks[key](context.ForTask(key, checkpoint,
                        new(checked(seed.Attempt + (uint)number - 1), seed.SavedAt)));
                }
                if (copied.Outcome == MockOutcome.Timeout)
                    return Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
                if (copied.Outcome == MockOutcome.Throw)
                    throw new InvalidOperationException(copied.ErrorMessage);
                if (copied.Outcome == MockOutcome.Fail ||
                    (copied.Outcome == MockOutcome.FailThenSucceed && number <= copied.FailuresBeforeSuccess))
                    throw new TaskFailureException(copied.ErrorMessage, copied.Retryable, copied.Category);
                foreach (var (outputKey, value) in copied.Outputs ?? ImmutableDictionary<string, JsonElement>.Empty)
                    context.Outputs.Set(outputKey, value, OutputJsonContext.Default.JsonElement);
                return Task.CompletedTask;
            });
        }
        var mocked = new WorkflowDefinition(workflow.Name, workflow.Version, workflow.Nodes, handlers.ToImmutable(),
            workflow.Variables, workflow.Metadata, workflow.RunTimeout, workflow.Placement);
        var clock = new DryRunClock();
        foreach (var mock in specified.Values)
            if (mock.Checkpoint is { } seed && seed.SavedAt > clock.GetUtcNow())
                clock.AdvanceTo(seed.SavedAt);
        var result = await new LocalEngine(mocked, spec.Variables, output: TextWriter.Null, timeProvider: clock)
            .RunAsync(cancellationToken).ConfigureAwait(false);
        return new(result.Snapshot, result.Events, result.Attempts.Select(a => new DryRunAttempt(
            a.TaskKey, a.Attempt, a.StartedAt, a.EndedAt, a.Status, a.Failure, a.Error?.GetType().Name, a.Error?.Message))
            .ToImmutableArray(), result.Outputs);
    }

    public static void ValidateMock(string key, TaskMock mock)
    {
        if (mock is null || !Enum.IsDefined(mock.Outcome) || !Enum.IsDefined(mock.Category) || mock.FailuresBeforeSuccess < 0 ||
            string.IsNullOrWhiteSpace(mock.ErrorMessage) ||
            (mock.Outputs?.Keys.Any(string.IsNullOrWhiteSpace) ?? false) ||
            (mock.Outputs?.Values.Any(v => v.ValueKind == JsonValueKind.Undefined) ?? false))
            throw new ArgumentException($"Mock task '{key}' contains an invalid outcome, category, count, message or output key.");
        if ((mock.Outcome == MockOutcome.ExecuteCheckpointed) != (mock.Checkpoint is not null) ||
            (mock.Checkpoint is not null && mock.Outputs is not null))
            throw new ArgumentException($"Mock task '{key}' requires an explicit checkpoint execution outcome and seed, without mock outputs.");
        if (mock.Checkpoint is { } seed)
        {
            if (seed.State.ValueKind == JsonValueKind.Undefined || seed.Sequence <= 0 || seed.Attempt < 2 ||
                seed.SavedAt.Offset != TimeSpan.Zero || seed.SavedAt.UtcTicks % 10 != 0)
                throw new ArgumentException($"Mock task '{key}' contains an invalid checkpoint seed.");
            CheckpointProtocol.ValidateState(JsonSerializer.SerializeToUtf8Bytes(seed.State, OutputJsonContext.Default.JsonElement));
        }
    }
}

internal sealed class DryRunClock : TimeProvider
{
    private long ticks = DateTimeOffset.UnixEpoch.UtcTicks;
    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
    public void AdvanceTo(DateTimeOffset at)
    {
        if (at.Offset != TimeSpan.Zero || at < GetUtcNow())
            throw new ArgumentOutOfRangeException(nameof(at), "Virtual time must advance monotonically in UTC.");
        Interlocked.Exchange(ref ticks, at.UtcTicks);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(DryRunSpec))]
[JsonSerializable(typeof(DryRunLog))]
public partial class DryRunJsonContext : JsonSerializerContext;
