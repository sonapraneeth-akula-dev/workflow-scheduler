using System.Collections.Immutable;
using System.Text.Json;
using Wf.Contracts.Definition;
using Wf.Sdk.Hosting;

namespace Wf.Sdk.Testing;

/// <summary>
/// Immutable task mocks using the SDK dry-run wire contract. Keys are stable node/task keys.
/// Unspecified tasks require an explicit default; only WithCheckpoint opts a named real delegate into execution.
/// </summary>
public sealed class MockSpec
{
    private readonly DryRunSpec spec;

    public MockSpec(TaskMock? defaultMock = null) =>
        spec = new(ImmutableDictionary<string, TaskMock>.Empty,
            defaultMock is null ? null : Copy(defaultMock), ImmutableDictionary<string, JsonElement>.Empty);

    private MockSpec(DryRunSpec spec) => this.spec = spec;

    public MockSpec WithTask(string taskKey, TaskMock mock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskKey);
        var parts = taskKey.Split('/');
        if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Mock task keys must use node/task.", nameof(taskKey));
        return new(spec with { Tasks = spec.Tasks!.SetItem(taskKey, Copy(mock)) });
    }

    public MockSpec WithDefault(TaskMock mock) => new(spec with { Default = Copy(mock) });

    public MockSpec WithCheckpoint(string taskKey, CheckpointMock checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        return WithTask(taskKey, new(MockOutcome.ExecuteCheckpointed, Checkpoint: checkpoint.Seed));
    }

    public MockSpec WithVariable(string key, JsonElement value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (value.ValueKind == JsonValueKind.Undefined)
            throw new ArgumentException("Mock variables require a defined JSON value.", nameof(value));
        return new(spec with { Variables = spec.Variables!.SetItem(key, value.Clone()) });
    }

    public MockSpec Succeed(string taskKey, IReadOnlyDictionary<string, JsonElement>? outputs = null) =>
        WithTask(taskKey, new(Outputs: Outputs(outputs)));

    public MockSpec Fail(string taskKey, string errorMessage = "Mock task failure.", bool retryable = true,
        FailureCategory category = FailureCategory.TaskError) =>
        WithTask(taskKey, new(MockOutcome.Fail, ErrorMessage: errorMessage, Category: category, Retryable: retryable));

    public MockSpec Timeout(string taskKey) => WithTask(taskKey, new(MockOutcome.Timeout));

    public MockSpec Throw(string taskKey, string errorMessage = "Mock task failure.") =>
        WithTask(taskKey, new(MockOutcome.Throw, ErrorMessage: errorMessage));

    /// <summary>Fails the first N attempts; success still requires sufficient workflow retry budget.</summary>
    public MockSpec FailNThenSucceed(string taskKey, int failures, IReadOnlyDictionary<string, JsonElement>? outputs = null,
        string errorMessage = "Mock task failure.", bool retryable = true,
        FailureCategory category = FailureCategory.TaskError) =>
        WithTask(taskKey, new(MockOutcome.FailThenSucceed, Outputs(outputs), failures, errorMessage, category, retryable));

    public DryRunSpec ToDryRunSpec() => spec;

    public string ToJson() => JsonSerializer.Serialize(spec, DryRunJsonContext.Default.DryRunSpec);

    private static ImmutableDictionary<string, JsonElement>? Outputs(IReadOnlyDictionary<string, JsonElement>? outputs) =>
        outputs?.ToImmutableDictionary(StringComparer.Ordinal);

    private static TaskMock Copy(TaskMock mock)
    {
        ArgumentNullException.ThrowIfNull(mock);
        DryRunHost.ValidateMock("task", mock);
        return mock with
        {
            Outputs = mock.Outputs?.ToImmutableDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal),
            Checkpoint = mock.Checkpoint is { } checkpoint ? checkpoint with { State = checkpoint.State.Clone() } : null
        };
    }
}
