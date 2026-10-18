using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wf.Agent.Hosts;
using Wf.Agent.Journal;
using Wf.Agent.Secrets;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Definition;
using Wf.TaskHost.Fixture;

namespace Wf.Agent.Tests;

public sealed class SecretExecutionTests : IDisposable
{
    private readonly string root = Path.GetFullPath(Path.Combine(".agent-artifacts", "secret-stores-3adec91f",
        "tests", Guid.NewGuid().ToString("N")));
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static TaskHostPackage Package(bool defaults = false) => new("1.0.0", "dotnet",
        ["exec", "--runtimeconfig", Path.ChangeExtension(typeof(SecretExecutionTests).Assembly.Location, ".runtimeconfig.json"),
            "--depsfile", Path.ChangeExtension(typeof(SecretExecutionTests).Assembly.Location, ".deps.json"),
            typeof(FixtureMarker).Assembly.Location, .. defaults ? new[] { "--secret-default-fixture" } : []], AppContext.BaseDirectory);

    [Theory]
    [InlineData("secret", false)]
    [InlineData("secretFailure", false)]
    [InlineData("secret", true)]
    public async Task Actual_protected_store_resolves_only_at_attempt_start_and_masks_UTF8_multichunk_logs_errors_and_journal(
        string task, bool defaults)
    {
        var local = new LocalSecretStore(Path.Combine(root, "values"));
        var store = new RecordingStore(local);
        var logger = new Recorder();
        var messages = new ConcurrentQueue<string>();
        await using var hosts = new TaskHostPool(logger, secretStore: store, containmentDirectory: Path.Combine(root, "host-lifetimes"));
        var package = Package(defaults);
        await hosts.PrewarmAsync(package, Token);
        Assert.Empty(store.Reads);
        var reference = new SecretReference(defaults ? "secret://local/default-token" : "secret://local/credential");
        const string value = "secret-🔑-clé-multi-chunk-value";
        await local.WriteAsync(reference, value, Token);
        var dispatch = Dispatch(task);
        if (!defaults) dispatch.VariablesJson = ByteString.CopyFromUtf8(
            """{"credential":{"Name":"secret://local/credential"}}""");
        using var outbox = new AgentOutbox(new ResultJournal(Path.Combine(root, "journal"), Guid.NewGuid().ToString()));
        var result = await hosts.ExecuteAsync(package, dispatch, cancellationToken: Token,
            captureLog: (_, text) => { messages.Enqueue(text); return Task.CompletedTask; },
            journalResult: completion => outbox.Enqueue(new AgentMessage { TaskCompleted = completion }));
        Assert.Equal(reference.Name, Assert.Single(store.Reads));
        Assert.Equal(task == "secret" ? TaskOutcome.Succeeded : TaskOutcome.Failed, result.Outcome);
        Assert.Equal(result, outbox.NextAfter(0)!.TaskCompleted);
        using var outputs = JsonDocument.Parse(result.OutputsJson.Memory);
        Assert.Equal(reference.Name, outputs.RootElement.GetProperty("reference").GetString());
        Assert.Equal("***", outputs.RootElement.GetProperty("value").GetString());
        Assert.Equal("***", outputs.RootElement.GetProperty("encoded").GetString());
        Assert.Equal(value.Length, outputs.RootElement.GetProperty("length").GetInt32());
        Assert.True(outputs.RootElement.GetProperty("launchClean").GetBoolean());
        Assert.NotEqual(Environment.ProcessId, outputs.RootElement.GetProperty("pid").GetInt32());
        if (result.Error is not null) Assert.Contains("***", result.Error.Message);
        Assert.Contains(messages, text => text.StartsWith("out *** tail", StringComparison.Ordinal));
        Assert.Contains(messages, text => text.StartsWith("ctx *** ***", StringComparison.Ordinal));
        Assert.Contains(messages, text => text == "***");
        outbox.Dispose();
        var durable = string.Join("\n", Directory.GetFiles(Path.Combine(root, "journal"), "*", SearchOption.AllDirectories)
            .Select(File.ReadAllBytes).Select(Encoding.UTF8.GetString));
        var combined = string.Join("\n", messages.Concat(logger.Messages)) + durable + result;
        Assert.DoesNotContain(value, combined);
        Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(value)), combined);
        Assert.Empty(hosts.WarmProcessIds);
    }

    [Theory]
    [InlineData("secret://local/missing")]
    [InlineData("secret://unconfigured/missing")]
    public async Task Actual_missing_secret_fails_correct_attempt_nonretryable_ValidationError_before_user_delegate(string reference)
    {
        var local = new LocalSecretStore(Path.Combine(root, "values"));
        await using var hosts = new TaskHostPool(NullLogger<TaskHostPool>.Instance, secretStore: local, containmentDirectory: Path.Combine(root, "host-lifetimes"));
        var dispatch = Dispatch();
        dispatch.VariablesJson = ByteString.CopyFromUtf8(JsonSerializer.Serialize(new { credential = new { Name = reference } }));
        var result = await hosts.ExecuteAsync(Package(), dispatch, cancellationToken: Token);
        Assert.Equal(dispatch.Task, result.Task);
        Assert.Equal(TaskOutcome.Failed, result.Outcome);
        Assert.Equal("ValidationError", result.Error.Category);
        Assert.True(result.Error.NonRetryable);
        Assert.Equal($"Secret '{reference}' is missing or unavailable.", result.Error.Message);
        Assert.True(result.OutputsJson.IsEmpty);
    }

    [Fact]
    public async Task Same_reference_is_reloaded_for_every_attempt_not_cached_with_a_prewarmed_host()
    {
        var local = new LocalSecretStore(Path.Combine(root, "values"));
        var store = new RecordingStore(local);
        await using var hosts = new TaskHostPool(NullLogger<TaskHostPool>.Instance, secretStore: store, containmentDirectory: Path.Combine(root, "host-lifetimes"));
        foreach (var value in new[] { "first", "second-distinct-value" })
        {
            await hosts.PrewarmAsync(Package(defaults: true), Token);
            await local.WriteAsync(new("secret://local/default-token"), value, Token);
            var result = await hosts.ExecuteAsync(Package(defaults: true), Dispatch(), cancellationToken: Token);
            Assert.Equal(TaskOutcome.Succeeded, result.Outcome);
            using var output = JsonDocument.Parse(result.OutputsJson.Memory);
            Assert.Equal(value.Length, output.RootElement.GetProperty("length").GetInt32());
        }
        Assert.Equal(2, store.Reads.Count);
    }

    [Fact]
    public async Task Invalid_reference_cannot_call_provider_and_unprovided_reference_cannot_be_read_by_SDK()
    {
        var store = new RecordingStore(new LocalSecretStore(Path.Combine(root, "values")));
        await using var hosts = new TaskHostPool(NullLogger<TaskHostPool>.Instance, secretStore: store, containmentDirectory: Path.Combine(root, "host-lifetimes"));
        var dispatch = Dispatch();
        dispatch.SecretRefs.Add("secret://local/../escape");
        var invalid = await hosts.ExecuteAsync(Package(), dispatch, cancellationToken: Token);
        Assert.Equal("ValidationError", invalid.Error.Category);
        Assert.True(invalid.Error.NonRetryable);
        Assert.Empty(store.Reads);
        dispatch = Dispatch();
        dispatch.VariablesJson = ByteString.CopyFromUtf8("""{"credential":{"Name":"secret://local/../escape"}}""");
        invalid = await hosts.ExecuteAsync(Package(), dispatch, cancellationToken: Token);
        Assert.Equal("ValidationError", invalid.Error.Category);
        Assert.True(invalid.Error.NonRetryable);
        Assert.Empty(store.Reads);
        var missing = new Wf.Sdk.TaskContext();
        var error = Assert.Throws<Wf.Contracts.TaskFailureException>(() => missing.Secret("not-declared"));
        Assert.False(error.Retryable);
        Assert.Equal(FailureCategory.ValidationError, error.Category);
    }

    [Fact]
    public async Task Only_selected_task_declarations_and_explicit_attempt_references_are_resolved()
    {
        var local = new LocalSecretStore(Path.Combine(root, "values"));
        await local.WriteAsync(new("secret://local/task-token"), "task-value", Token);
        await local.WriteAsync(new("secret://local/explicit"), "explicit-value", Token);
        var store = new RecordingStore(local);
        await using var hosts = new TaskHostPool(NullLogger<TaskHostPool>.Instance, secretStore: store, containmentDirectory: Path.Combine(root, "host-lifetimes"));
        Assert.Equal(TaskOutcome.Succeeded, (await hosts.ExecuteAsync(Package(), Dispatch("outputs"), cancellationToken: Token)).Outcome);
        Assert.Empty(store.Reads);
        var operation = await hosts.ExecuteAsync(Package(), Dispatch("secretOperation"), cancellationToken: Token);
        Assert.Equal("secret://local/task-token", Assert.Single(store.Reads));
        Assert.Equal("ValidationError", operation.Error.Category);
        Assert.Contains("RemoteOperationRequiresCheckpoint", operation.Error.Message);
        Assert.True(operation.Error.NonRetryable);
        var dispatch = Dispatch("outputs");
        dispatch.SecretRefs.Add("secret://local/explicit");
        Assert.Equal(TaskOutcome.Succeeded, (await hosts.ExecuteAsync(Package(), dispatch, cancellationToken: Token)).Outcome);
        Assert.Equal(["secret://local/task-token", "secret://local/explicit"], store.Reads.ToArray());
    }

    [Fact]
    public async Task Partial_resolution_failure_also_masks_already_resolved_values_before_returning_an_error()
    {
        var local = new LocalSecretStore(Path.Combine(root, "values"));
        await local.WriteAsync(new("secret://local/first"), "second-missing", Token);
        var store = new RecordingStore(local);
        await using var hosts = new TaskHostPool(NullLogger<TaskHostPool>.Instance, secretStore: store, containmentDirectory: Path.Combine(root, "host-lifetimes"));
        var dispatch = Dispatch("outputs");
        dispatch.SecretRefs.AddRange(["secret://local/first", "second-missing"]);
        var result = await hosts.ExecuteAsync(Package(), dispatch, cancellationToken: Token);
        Assert.Equal(["secret://local/first", "second-missing"], store.Reads.ToArray());
        Assert.Equal("ValidationError", result.Error.Category);
        Assert.True(result.Error.NonRetryable);
        Assert.Equal("Secret '***' is missing or unavailable.", result.Error.Message);
        Assert.DoesNotContain("second-missing", result.ToString());
    }

    [Fact]
    public async Task Expired_attempt_keeps_existing_timeout_semantics_without_resolving_or_executing_user_code()
    {
        var store = new RecordingStore(new LocalSecretStore(Path.Combine(root, "values")));
        await using var hosts = new TaskHostPool(NullLogger<TaskHostPool>.Instance, secretStore: store, containmentDirectory: Path.Combine(root, "host-lifetimes"));
        await hosts.PrewarmAsync(Package(), Token);
        var dispatch = Dispatch();
        dispatch.DeadlineAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(-1));
        var result = await hosts.ExecuteAsync(Package(), dispatch, cancellationToken: Token);
        Assert.Equal(TaskOutcome.TimedOut, result.Outcome);
        Assert.Equal("Timeout", result.Error.Category);
        Assert.False(result.Error.NonRetryable);
        Assert.Empty(store.Reads);
        Assert.True(result.OutputsJson.IsEmpty);
    }

    private static Dispatch Dispatch(string task = "secret") => new()
    {
        Task = new AttemptRef { RunId = Guid.NewGuid().ToString(), NodeKey = "work", TaskKey = task, Attempt = 1 },
        Version = "1.0.0", DeadlineAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(30))
    };

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }

    private sealed class RecordingStore(ISecretStore inner) : ISecretStore
    {
        public ConcurrentQueue<string> Reads { get; } = new();
        public ValueTask<string> ResolveAsync(SecretReference reference, CancellationToken cancellationToken = default)
        {
            Reads.Enqueue(reference.Name);
            return inner.ResolveAsync(reference, cancellationToken);
        }
    }

    private sealed class Recorder : ILogger<TaskHostPool>
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Enqueue(formatter(state, exception));
    }
}
