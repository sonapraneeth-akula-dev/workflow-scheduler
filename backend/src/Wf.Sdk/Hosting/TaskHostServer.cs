using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Wf.Contracts;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Definition;
using Wf.Contracts.Host.V1;
using Wf.Validation;

namespace Wf.Sdk.Hosting;

public static class TaskHostServer
{
    [RequiresDynamicCode("Task hosts run framework-dependent workflow IL, not Native AOT.")]
    [RequiresUnreferencedCode("The gRPC task-host server requires untrimmed workflow IL.")]
    public static async Task<int> RunAsync(WorkflowDefinition workflow, string socketPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentException.ThrowIfNullOrWhiteSpace(socketPath);
        if (!Path.IsPathFullyQualified(socketPath) || System.Text.Encoding.UTF8.GetByteCount(socketPath) >= 108)
            throw new ArgumentException("The task-host socket must be an absolute path shorter than 108 UTF-8 bytes.", nameof(socketPath));
        if (File.Exists(socketPath))
            throw new IOException("The task-host socket path already exists.");
        // Private IPC needs no application configuration watchers or public-web middleware defaults.
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrelCore().ConfigureKestrel(options =>
            options.ListenUnixSocket(socketPath, listen => listen.Protocols = HttpProtocols.Http2));
        builder.Services.AddRoutingCore();
        builder.Services.AddGrpc(options => { options.MaxReceiveMessageSize = 1024 * 1024; options.MaxSendMessageSize = 1024 * 1024; });
        builder.Services.AddSingleton(new TaskHostService(workflow));
        await using var app = builder.Build();
        app.MapGrpcService<TaskHostService>();
        await app.RunAsync(cancellationToken);
        return 0;
    }
}

internal sealed class TaskHostService(WorkflowDefinition workflow) : TaskHost.TaskHostBase
{
    private readonly object sync = new();
    private AttemptRef? bound;
    private Task<TaskCompleted>? execution;
    private CancellationTokenSource? attemptCancellation;
    private bool timedOut;
    private bool cancelled;

    public override Task<HostReady> Ready(ReadyRequest request, ServerCallContext context)
    {
        lock (sync)
            return Task.FromResult(new HostReady
            {
                ProtocolVersion = 1, ProcessId = (uint)Environment.ProcessId,
                WorkflowVersion = workflow.Version, Bound = bound is not null
            });
    }

    public override Task<TaskCompleted> Execute(ExecuteRequest request, ServerCallContext context)
    {
        lock (sync)
        {
            if (bound is not null)
                throw new RpcException(new Status(StatusCode.FailedPrecondition, "Task host is already bound to an attempt."));
            var dispatch = request.Dispatch;
            if (dispatch?.Task is not { } task || !Guid.TryParse(task.RunId, out _) ||
                string.IsNullOrWhiteSpace(task.NodeKey) || string.IsNullOrWhiteSpace(task.TaskKey) || task.Attempt == 0 ||
                dispatch.Version != workflow.Version || dispatch.DeadlineAt is null)
                throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid attempt identity, version or deadline."));
            var node = workflow.Nodes.FirstOrDefault(node => node.Key == task.NodeKey);
            if (node is null || !node.Tasks.Any(candidate => candidate.Key == task.TaskKey))
                throw new RpcException(new Status(StatusCode.InvalidArgument, "Task is not declared in this workflow."));
            try
            {
                var variables = VariableBinder.Bind(workflow.Variables, ReadObject(dispatch.VariablesJson));
                var inputs = ReadObject(dispatch.InputsJson).ToDictionary(pair => pair.Key,
                    pair => ReadOutputs(pair.Key, pair.Value), StringComparer.Ordinal);
                if (inputs.Keys.Any(key => !node.Inputs.Any(input => input.Node == key)))
                    throw new ArgumentException("Inputs contain an undeclared source node.");
                var outputs = new NodeOutputs(node.Key);
                foreach (var pair in ReadObject(request.NodeOutputsJson))
                    outputs[pair.Key] = pair.Value;
                var deadline = dispatch.DeadlineAt.ToDateTimeOffset();
                bound = task.Clone();
                attemptCancellation = new CancellationTokenSource();
                execution = ExecuteTaskAsync(dispatch, node, variables, inputs, outputs, deadline);
                return execution;
            }
            catch (Exception exception) when (exception is ArgumentException or JsonException or InvalidOperationException)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, exception.Message));
            }
        }
    }

    public override Task<HostReply> Cancel(CancelRequest request, ServerCallContext context)
    {
        lock (sync)
        {
            CheckAttempt(request.Task);
            if (!execution!.IsCompleted)
            {
                cancelled = true;
                timedOut |= request.TimedOut;
                // User cancellation callbacks run outside the RPC worker and cannot block Cancel.
                _ = CancelAttemptAsync();
            }
            return Task.FromResult(new HostReply());
        }
    }

    public override Task<HostReply> Acknowledge(AcknowledgeRequest request, ServerCallContext context)
    {
        lock (sync)
        {
            CheckAttempt(request.Task);
            if (!execution!.IsCompletedSuccessfully)
                throw new RpcException(new Status(StatusCode.FailedPrecondition, "Attempt has no completed result."));
            context.GetHttpContext().Response.OnCompleted(() =>
            {
                context.GetHttpContext().RequestServices
                    .GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>().StopApplication();
                return Task.CompletedTask;
            });
            return Task.FromResult(new HostReply());
        }
    }

    private void CheckAttempt(AttemptRef? task)
    {
        if (bound is null || task is null || !bound.Equals(task))
            throw new RpcException(new Status(StatusCode.NotFound, "Attempt is not bound to this host."));
    }

    private async Task CancelAttemptAsync()
    {
        try { await attemptCancellation!.CancelAsync(); }
        catch (AggregateException exception) { Console.Error.WriteLine($"Task cancellation callbacks failed: {exception}"); }
    }

    private async Task<TaskCompleted> ExecuteTaskAsync(Dispatch dispatch, NodeDefinition node,
        IReadOnlyDictionary<string, object> variables, IReadOnlyDictionary<string, NodeOutputs> inputs,
        NodeOutputs outputs, DateTimeOffset deadline)
    {
        // Ensure Execute returns before user code can synchronously block the gRPC worker.
        await Task.Yield();
        var watch = Stopwatch.StartNew();
        using var deadlineCancellation = new CancellationTokenSource();
        using var deadlineStop = new CancellationTokenSource();
        var remaining = deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) deadlineCancellation.Cancel();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(attemptCancellation!.Token, deadlineCancellation.Token);
        var deadlineTimer = remaining <= TimeSpan.Zero ? Task.CompletedTask : CancelAtDeadlineAsync();
        var result = new TaskCompleted { Task = dispatch.Task.Clone() };
        try
        {
            var taskContext = new TaskContext(linked.Token, variables, outputs, inputs, node.Inputs);
            await new TaskRegistry(workflow).ExecuteAsync($"{node.Key}/{dispatch.Task.TaskKey}", taskContext);
            linked.Token.ThrowIfCancellationRequested();
            result.Outcome = TaskOutcome.Succeeded;
        }
        catch (Exception exception)
        {
            bool wasCancelled, wasTimedOut;
            lock (sync) { wasCancelled = cancelled; wasTimedOut = timedOut || deadlineCancellation.IsCancellationRequested; }
            result.Outcome = wasTimedOut ? TaskOutcome.TimedOut : wasCancelled ? TaskOutcome.Cancelled : TaskOutcome.Failed;
            var category = wasTimedOut ? FailureCategory.Timeout : wasCancelled || exception is OperationCanceledException ?
                FailureCategory.Aborted : exception is TaskFailureException failure ? failure.Category : FailureCategory.TaskError;
            result.Error = new TaskError
            {
                Category = category.ToString(), Message = exception.Message,
                NonRetryable = category == FailureCategory.Aborted || exception is TaskFailureException { Retryable: false }
            };
        }
        finally
        {
            await deadlineStop.CancelAsync();
            await deadlineTimer;
        }
        result.OutputsJson = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes(outputs.Values,
            OutputJsonContext.Default.ImmutableDictionaryStringJsonElement));
        result.At = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
        result.ResourceSummary = new ResourceSummary { ElapsedMs = (ulong)watch.ElapsedMilliseconds };
        return result;

        async Task CancelAtDeadlineAsync()
        {
            try
            {
                while (deadline > DateTimeOffset.UtcNow)
                {
                    var delay = deadline - DateTimeOffset.UtcNow;
                    if (delay > TimeSpan.Zero)
                        await Task.Delay(delay > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : delay, deadlineStop.Token);
                }
                try { await deadlineCancellation.CancelAsync(); }
                catch (AggregateException exception) { Console.Error.WriteLine($"Task deadline cancellation callbacks failed: {exception}"); }
            }
            catch (OperationCanceledException) when (deadlineStop.IsCancellationRequested) { }
        }
    }

    private static Dictionary<string, JsonElement> ReadObject(ByteString bytes)
    {
        if (bytes.IsEmpty) return new(StringComparer.Ordinal);
        using var document = JsonDocument.Parse(bytes.Memory);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Task-host inputs, outputs and variables must be JSON objects.");
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
            if (!values.TryAdd(property.Name, property.Value.Clone()))
                throw new ArgumentException($"Duplicate JSON key '{property.Name}'.");
        return values;
    }

    private static NodeOutputs ReadOutputs(string node, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException($"Input node '{node}' outputs must be a JSON object.");
        var outputs = new NodeOutputs(node);
        foreach (var property in value.EnumerateObject())
        {
            if (outputs.Values.ContainsKey(property.Name))
                throw new ArgumentException($"Duplicate output key '{node}/{property.Name}'.");
            outputs[property.Name] = property.Value;
        }
        return outputs;
    }
}
