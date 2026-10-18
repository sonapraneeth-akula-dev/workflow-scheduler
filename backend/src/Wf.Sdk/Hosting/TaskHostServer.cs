using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
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
using Wf.Contracts.Resources;
using Wf.Contracts.Secrets;
using Wf.Engine.Core;
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
        if (Environment.GetEnvironmentVariable("WF_TASK_HOST_GUARD") == "1")
            return await WindowsHostGuard.RunAsync(workflow.Version, cancellationToken);
        if (File.Exists(socketPath))
            throw new IOException("The task-host socket path already exists.");
        if (LinuxHostSupervisor.Required)
            return await LinuxHostSupervisor.RunAsync(socketPath, cancellationToken);
        using var lifetimeJob = WindowsHostJobs.OpenLifetime();
        Console.OutputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        // Private IPC needs no application configuration watchers or public-web middleware defaults.
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrelCore().ConfigureKestrel(options =>
            options.ListenUnixSocket(socketPath, listen => listen.Protocols = HttpProtocols.Http2));
        builder.Services.AddRoutingCore();
        builder.Services.AddGrpc(options => { options.MaxReceiveMessageSize = 4 * 1024 * 1024; options.MaxSendMessageSize = 1024 * 1024; });
        builder.Services.AddSingleton(_ => new TaskHostService(workflow));
        await using var app = builder.Build();
        var token = Environment.GetEnvironmentVariable("WF_TASK_HOST_CONTAINMENT_TOKEN");
        if (token is not null)
        {
            if (!Guid.TryParseExact(token, "N", out _))
                throw new InvalidOperationException("Task-host authentication token is invalid.");
            var expected = Encoding.ASCII.GetBytes(token);
            app.Use(async (context, next) =>
            {
                var supplied = context.Request.Headers["x-wf-host-token"].ToString();
                if (!CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(supplied)))
                {
                    context.Response.StatusCode = 403;
                    return;
                }
                await next(context);
            });
        }
        app.MapGrpcService<TaskHostService>();
        await app.RunAsync(cancellationToken);
        return 0;
    }
}

internal sealed class TaskHostService(WorkflowDefinition workflow) : TaskHost.TaskHostBase, IDisposable
{
    private readonly object sync = new();
    private AttemptRef? bound;
    private Dispatch? boundDispatch;
    private IReadOnlyDictionary<string, string>? boundSecrets;
    private string[] boundRedactionValues = [];
    private Task<TaskCompleted>? execution;
    private CancellationTokenSource? attemptCancellation;
    private bool timedOut;
    private bool cancelled;
    private bool limitsApplied;
    private Microsoft.Win32.SafeHandles.SafeFileHandle? limitsJob;
    private readonly Channel<byte> checkpoints = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        { FullMode = BoundedChannelFullMode.DropWrite });
    private CheckpointSaved? pendingCheckpoint;
    private TaskCompletionSource? checkpointAcknowledgement;
    private int checkpointReader;
    private readonly Channel<byte> progress = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        { FullMode = BoundedChannelFullMode.DropWrite });
    private TaskProgress? pendingProgress;
    private TaskCompletionSource? progressAcknowledgement;
    private int progressReader;
    private readonly TaskCompletionSource<AttemptSpool?> spoolReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private AttemptSpool? spool;
    private long lastNativeSample;
    private int recordReader;

    public override Task<HostReady> Ready(ReadyRequest request, ServerCallContext context)
    {
        lock (sync)
        {
            var ready = new HostReady
            {
                ProtocolVersion = 9, ProcessId = uint.TryParse(
                    Environment.GetEnvironmentVariable("WF_TASK_HOST_SUPERVISOR_PID"), out var supervisor)
                    ? supervisor : (uint)Environment.ProcessId,
                WorkflowVersion = workflow.Version, Bound = bound is not null,
                ReservationEpoch = boundDispatch?.ReservationEpoch ?? 0
            };
            if (bound is not null) ready.Attempt = bound.Clone();
            return Task.FromResult(ready);
        }
    }

    public override Task<HostReply> ApplyLimits(ApplyLimitsRequest request, ServerCallContext context)
    {
        lock (sync)
        {
            if (bound is not null || limitsApplied)
                throw new RpcException(new Status(StatusCode.FailedPrecondition, "Task-host resource limits are already bound."));
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    if (request.LinuxCgroupPath.Length != 0)
                        throw new InvalidDataException("Windows task host cannot bind a Linux workload cgroup.");
                    limitsJob = WindowsHostJobs.OpenLimits(request.WindowsJobName);
                }
                else
                {
                    if (request.WindowsJobName.Length != 0)
                        throw new InvalidDataException("Linux task host cannot bind a Windows resource Job.");
                    LinuxWorkloadCgroup.Join(request.LinuxCgroupPath);
                }
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or
                UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            { throw new RpcException(new Status(StatusCode.FailedPrecondition, "Cannot join workload resource limits: " + error.Message)); }
            limitsApplied = true;
            return Task.FromResult(new HostReply());
        }
    }

    public void Dispose() => limitsJob?.Dispose();

    public override Task<SecretReferences> RequiredSecrets(ExecuteRequest request, ServerCallContext context)
    {
        lock (sync)
        {
            if (bound is not null)
            {
                if (!boundDispatch!.Equals(request.Dispatch))
                    throw new RpcException(new Status(StatusCode.FailedPrecondition, "Recovered dispatch does not match this host."));
                var recovered = new SecretReferences();
                recovered.Names.Add(boundSecrets!.Keys);
                foreach (var pair in boundSecrets) recovered.RecoveredValues.Add(pair.Key, pair.Value);
                recovered.RecoveredRedactionValues.Add(boundRedactionValues);
                return Task.FromResult(recovered);
            }
            try
            {
                var dispatch = request.Dispatch;
                var node = FindNode(dispatch);
                var supplied = ReadObject(dispatch.VariablesJson);
                if (workflow.Variables.Where(schema => schema.Type == VariableType.Secret).Any(schema =>
                    supplied.TryGetValue(schema.Key, out var value) ? !SecretReference.IsValidValue(value) :
                    schema.Required && schema.Default is null))
                    throw new RpcException(new Status(StatusCode.InvalidArgument, "Task secret references are invalid."));
                var variables = VariableBinder.Bind(workflow.Variables, supplied);
                var result = new SecretReferences();
                try { result.Names.Add(References(dispatch, node, variables)); }
                catch (ArgumentException)
                { throw new RpcException(new Status(StatusCode.InvalidArgument, "Task secret references are invalid.")); }
                return Task.FromResult(result);
            }
            catch (Exception error) when (error is ArgumentException or JsonException or InvalidOperationException)
            { throw new RpcException(new Status(StatusCode.InvalidArgument, "Task inputs or variables are invalid.")); }
        }
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
                var references = References(dispatch, node, variables);
                if (request.ResolvedSecrets.Count != references.Length || references.Any(reference =>
                    !request.ResolvedSecrets.TryGetValue(reference, out var value) || string.IsNullOrEmpty(value)))
                    throw new ArgumentException("Required task secrets are unavailable.");
                if (request.RedactionValues.Count > 128 || request.RedactionValues.Any(string.IsNullOrEmpty) ||
                    request.RedactionValues.Sum(Encoding.UTF8.GetByteCount) > 128 * 1024)
                    throw new ArgumentException("Task redaction values exceed their limits.");
                var deadline = dispatch.DeadlineAt.ToDateTimeOffset();
                bound = task.Clone();
                boundDispatch = dispatch.Clone();
                boundSecrets = request.ResolvedSecrets.ToDictionary();
                boundRedactionValues = request.RedactionValues.ToArray();
                attemptCancellation = new CancellationTokenSource();
                if (Environment.GetEnvironmentVariable("WF_TASK_HOST_ATTEMPT_DIRECTORY") is { } directory)
                    spool = new AttemptSpool(directory, dispatch);
                spoolReady.TrySetResult(spool);
                execution = ExecuteTaskAsync(dispatch, node, variables, inputs, outputs, deadline, request.ResolvedSecrets);
                return execution;
            }
            catch (Exception exception) when (exception is ArgumentException or JsonException or InvalidOperationException)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "Task execution inputs or secret bindings are invalid."));
            }
        }
    }

    public override Task<TaskCompleted> Resume(ExecuteRequest request, ServerCallContext context)
    {
        lock (sync)
        {
            CheckAttempt(request.Dispatch?.Task);
            if (!boundDispatch!.Equals(request.Dispatch) || request.ResolvedSecrets.Count != 0 || request.RedactionValues.Count != 0)
                throw new RpcException(new Status(StatusCode.FailedPrecondition, "Recovered dispatch does not match this host."));
            return execution!;
        }
    }

    private NodeDefinition FindNode(Dispatch? dispatch)
    {
        if (dispatch?.Task is not { } task || dispatch.Version != workflow.Version || dispatch.DeadlineAt is null ||
            !Guid.TryParse(task.RunId, out _) || string.IsNullOrWhiteSpace(task.NodeKey) ||
            string.IsNullOrWhiteSpace(task.TaskKey) || task.Attempt == 0)
            throw new ArgumentException("Invalid task identity.");
        return workflow.Nodes.FirstOrDefault(node => node.Key == task.NodeKey &&
            node.Tasks.Any(candidate => candidate.Key == task.TaskKey)) ??
            throw new ArgumentException("Task is not declared in this workflow.");
    }

    private static string[] References(Dispatch dispatch, NodeDefinition node, IReadOnlyDictionary<string, object> variables)
    {
        var references = variables.Values.OfType<SecretReference>().Select(reference => reference.Name)
            .Concat(dispatch.SecretRefs);
        if (node.Tasks.Single(task => task.Key == dispatch.Task.TaskKey).HttpOperation?.SecretReference is { } taskReference)
            references = references.Append(taskReference);
        var names = references.Distinct(StringComparer.Ordinal).ToArray();
        if (names.Length > 128 || names.Any(name => !SecretReference.IsValidName(name)))
            throw new ArgumentException("Task secret references are invalid.");
        return names;
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
            try { spool?.RequireAcknowledged(); }
            catch (InvalidOperationException error)
            { throw new RpcException(new Status(StatusCode.FailedPrecondition, error.Message)); }
            context.GetHttpContext().Response.OnCompleted(() =>
            {
                context.GetHttpContext().RequestServices
                    .GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>().StopApplication();
                return Task.CompletedTask;
            });
            return Task.FromResult(new HostReply());
        }
    }

    public override async Task Checkpoints(ReadyRequest request, IServerStreamWriter<CheckpointSaved> responseStream,
        ServerCallContext context)
    {
        if (Interlocked.Exchange(ref checkpointReader, 1) != 0)
            throw new RpcException(new Status(StatusCode.AlreadyExists, "Checkpoint stream is already attached."));
        try
        {
            checkpoints.Writer.TryWrite(0);
            await foreach (var _ in checkpoints.Reader.ReadAllAsync(context.CancellationToken))
            {
                CheckpointSaved? saved;
                lock (sync) saved = pendingCheckpoint?.Clone();
                if (saved is not null) await responseStream.WriteAsync(saved, context.CancellationToken);
            }
        }
        finally { Volatile.Write(ref checkpointReader, 0); }
    }

    public override Task<HostReply> ConfirmCheckpoint(CheckpointSaved request, ServerCallContext context)
    {
        lock (sync)
        {
            if (pendingCheckpoint is null || !pendingCheckpoint.Equals(request))
                throw new RpcException(new Status(StatusCode.FailedPrecondition, "Checkpoint acknowledgement does not match the pending save."));
            checkpointAcknowledgement!.TrySetResult();
            pendingCheckpoint = null;
            checkpointAcknowledgement = null;
            return Task.FromResult(new HostReply());
        }
    }

    private async Task PublishCheckpointAsync(CheckpointSaved saved, CancellationToken token)
    {
        var acknowledgement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (sync)
        {
            if (pendingCheckpoint is not null)
                throw new InvalidOperationException("An earlier checkpoint is still awaiting acknowledgement.");
            pendingCheckpoint = saved.Clone();
            checkpointAcknowledgement = acknowledgement;
        }
        checkpoints.Writer.TryWrite(0);
        await acknowledgement.Task.WaitAsync(token);
    }

    public override async Task Progress(ReadyRequest request, IServerStreamWriter<TaskProgress> responseStream,
        ServerCallContext context)
    {
        if (Interlocked.Exchange(ref progressReader, 1) != 0)
            throw new RpcException(new Status(StatusCode.AlreadyExists, "Progress stream is already attached."));
        try
        {
            progress.Writer.TryWrite(0);
            await foreach (var _ in progress.Reader.ReadAllAsync(context.CancellationToken))
            {
                TaskProgress? update;
                lock (sync) update = pendingProgress?.Clone();
                if (update is not null) await responseStream.WriteAsync(update, context.CancellationToken);
            }
        }
        finally { Volatile.Write(ref progressReader, 0); }
    }

    public override Task<HostReply> ConfirmProgress(TaskProgress request, ServerCallContext context)
    {
        lock (sync)
        {
            if (pendingProgress is null || !pendingProgress.Equals(request))
                throw new RpcException(new Status(StatusCode.FailedPrecondition, "Progress acknowledgement does not match the pending transition."));
            progressAcknowledgement!.TrySetResult();
            pendingProgress = null;
            progressAcknowledgement = null;
            return Task.FromResult(new HostReply());
        }
    }

    private async Task PublishProgressAsync(TaskProgress update, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _ = Wf.Contracts.Progress.TaskProgressProtocol.Validate(update);
        if (spool is not null)
        {
            spool.Progress(update);
            return;
        }
        using var journalDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var acknowledgement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (sync)
        {
            if (pendingProgress is not null)
                throw new InvalidOperationException("An earlier progress transition is still awaiting acknowledgement.");
            pendingProgress = update.Clone();
            progressAcknowledgement = acknowledgement;
        }
        progress.Writer.TryWrite(0);
        // Finish the journal handshake even if task cancellation arrives; cleanup may publish next.
        await acknowledgement.Task.WaitAsync(journalDeadline.Token);
    }

    public override async Task Records(RecordCursor request, IServerStreamWriter<AttemptRecord> responseStream,
        ServerCallContext context)
    {
        if (Interlocked.Exchange(ref recordReader, 1) != 0)
            throw new RpcException(new Status(StatusCode.AlreadyExists, "Attempt record stream is already attached."));
        try
        {
            var current = await spoolReady.Task.WaitAsync(context.CancellationToken);
            lock (sync) CheckAttempt(request.Task);
            if (current is null) return;
            var after = request.Sequence;
            while (true)
            {
                var batch = current.ReadAfter(after);
                foreach (var record in batch.Records)
                {
                    await responseStream.WriteAsync(record, context.CancellationToken);
                    after = record.Sequence;
                }
                if (batch.Records.Length != 0) continue;
                if (batch.Complete) return;
                await batch.Changed.WaitAsync(context.CancellationToken);
            }
        }
        finally { Volatile.Write(ref recordReader, 0); }
    }

    public override Task<HostReply> ConfirmRecords(RecordCursor request, ServerCallContext context)
    {
        lock (sync)
        {
            CheckAttempt(request.Task);
            try { (spool ?? throw new InvalidOperationException("Host has no durable attempt spool.")).Confirm(request.Sequence); }
            catch (Exception error) when (error is InvalidDataException or InvalidOperationException)
            { throw new RpcException(new Status(StatusCode.FailedPrecondition, error.Message)); }
            return Task.FromResult(new HostReply());
        }
    }

    public override async Task<HostReply> StoreResource(ResourceSample request, ServerCallContext context)
    {
        var current = await spoolReady.Task.WaitAsync(context.CancellationToken);
        lock (sync)
        {
            CheckAttempt(request.Task);
            if (request.ReservationEpoch != boundDispatch!.ReservationEpoch)
                throw new RpcException(new Status(StatusCode.FailedPrecondition, "Resource sample reservation does not match."));
            current?.Resource(request);
            Volatile.Write(ref lastNativeSample, Stopwatch.GetTimestamp());
            return new HostReply();
        }
    }

    public override Task<HostReply> SealRecords(RecordCursor request, ServerCallContext context)
    {
        lock (sync)
        {
            CheckAttempt(request.Task);
            spool?.SealRecords();
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
        NodeOutputs outputs, DateTimeOffset deadline, IReadOnlyDictionary<string, string> secrets)
    {
        // Ensure Execute returns before user code can synchronously block the gRPC worker.
        await Task.Yield();
        var watch = Stopwatch.StartNew();
        var redactor = new SecretRedactor(secrets.Values.Concat(boundRedactionValues));
        await using var capture = spool is null ? null : new TaskOutputCapture(spool, redactor);
        using var samplingStop = new CancellationTokenSource();
        var samples = spool is null ? Task.CompletedTask : SampleOfflineAsync();
        using var deadlineCancellation = new CancellationTokenSource();
        using var deadlineStop = new CancellationTokenSource();
        var remaining = deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) deadlineCancellation.Cancel();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(attemptCancellation!.Token, deadlineCancellation.Token);
        var deadlineTimer = remaining <= TimeSpan.Zero ? Task.CompletedTask : CancelAtDeadlineAsync();
        var result = new TaskCompleted { Task = dispatch.Task.Clone(), ReservationEpoch = dispatch.ReservationEpoch };
        var observedOutOfMemory = 0;
        EventHandler<FirstChanceExceptionEventArgs>? memoryHandler = dispatch.Limits?.RamMb > 0
            ? (_, args) =>
            {
                if (args.Exception is OutOfMemoryException) Interlocked.Exchange(ref observedOutOfMemory, 1);
            } : null;
        if (memoryHandler is not null) AppDomain.CurrentDomain.FirstChanceException += memoryHandler;
        try
        {
            var reportGrant = dispatch.Grants.SingleOrDefault(grant => grant.Kind == "reports");
            using var reportHttp = reportGrant is null ? null : new HttpClient(new SocketsHttpHandler
            {
                AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(10)
            })
            {
                DefaultRequestVersion = System.Net.HttpVersion.Version20,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Timeout = TimeSpan.FromSeconds(30)
            };
            var reports = reportHttp is null ? null : ReportUploadClient.Create(reportHttp, reportGrant!, dispatch.Task);
            var artifactGrant = dispatch.Grants.SingleOrDefault(grant => grant.Kind == "artifacts");
            using var artifactHttp = artifactGrant is null ? null : new HttpClient(new SocketsHttpHandler
            {
                AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(10)
            })
            {
                DefaultRequestVersion = System.Net.HttpVersion.Version20,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Timeout = Timeout.InfiniteTimeSpan
            };
            var artifacts = artifactHttp is null ? null : new ArtifactPublisher(artifactHttp, artifactGrant!, dispatch.Task);
            using var checkpointHttp = new HttpClient(new SocketsHttpHandler
            {
                AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(10)
            })
            {
                DefaultRequestVersion = System.Net.HttpVersion.Version20,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Timeout = TimeSpan.FromSeconds(30)
            };
            var checkpoint = CheckpointUploadClient.Create(checkpointHttp, dispatch, PublishCheckpointAsync, linked.Token);
            uint progressSequence = 0;
            var taskContext = new TaskContext(linked.Token, variables, outputs, inputs, node.Inputs,
                reports: reports, artifacts: artifacts, secrets: secrets, checkpoint: checkpoint,
                attempt: new(dispatch.Task.Attempt, dispatch.Checkpoint?.Reference.SavedAt.ToDateTimeOffset()),
                abort: dispatch.AbortJson.IsEmpty ? null : JsonSerializer.Deserialize(dispatch.AbortJson.Span,
                    AbortRecordJsonContext.Default.AbortRecord), runId: Guid.Parse(dispatch.Task.RunId))
            {
                ProgressPublisher = (phase, token) =>
                {
                    var now = DateTimeOffset.UtcNow;
                    return PublishProgressAsync(new TaskProgress
                    {
                        Task = dispatch.Task.Clone(), ReservationEpoch = dispatch.ReservationEpoch,
                        ProgressSequence = checked(++progressSequence), Phase = phase,
                        At = Timestamp.FromDateTimeOffset(new(now.UtcTicks / 10 * 10, TimeSpan.Zero))
                    }, token);
                }
            };
            await new TaskRegistry(workflow).ExecuteAsync($"{node.Key}/{dispatch.Task.TaskKey}", taskContext);
            linked.Token.ThrowIfCancellationRequested();
            result.Outcome = TaskOutcome.Succeeded;
        }
        catch (Exception exception)
        {
            bool wasCancelled, wasTimedOut;
            lock (sync) { wasCancelled = cancelled; wasTimedOut = timedOut || deadlineCancellation.IsCancellationRequested; }
            result.Outcome = wasTimedOut ? TaskOutcome.TimedOut : wasCancelled ? TaskOutcome.Cancelled : TaskOutcome.Failed;
            var category = exception is OutOfMemoryException && dispatch.Limits?.RamMb > 0
                ? FailureCategory.ResourceLimit : FailureClassifier.Classify(exception, FailureCategory.TaskError,
                timedOut: wasTimedOut, aborted: wasCancelled);
            result.Error = new TaskError
            {
                Category = category.ToString(), Message = exception.Message,
                NonRetryable = category is FailureCategory.Aborted or FailureCategory.ResourceLimit ||
                    exception is TaskFailureException { Retryable: false }
            };
        }
        finally
        {
            if (memoryHandler is not null) AppDomain.CurrentDomain.FirstChanceException -= memoryHandler;
            checkpoints.Writer.TryComplete();
            progress.Writer.TryComplete();
            await deadlineStop.CancelAsync();
            await deadlineTimer;
            await samplingStop.CancelAsync();
            await samples;
            if (capture is not null) await capture.CompleteAsync();
        }
        if (Volatile.Read(ref observedOutOfMemory) != 0)
        {
            result.Outcome = TaskOutcome.Failed;
            result.Error = new TaskError { Category = "ResourceLimit", NonRetryable = true,
                Message = "Task encountered an out-of-memory exception while executing with a RAM limit." };
        }
        result.OutputsJson = result.Error?.Category == "ResourceLimit" ? ByteString.Empty :
            ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes(outputs.Values,
                OutputJsonContext.Default.ImmutableDictionaryStringJsonElement));
        result.At = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
        result.ResourceSummary = new ResourceSummary { ElapsedMs = (ulong)watch.ElapsedMilliseconds };
        result = redactor.RedactResult(result);
        return spool?.Complete(result) ?? result;

        async Task SampleOfflineAsync()
        {
            using var process = Process.GetCurrentProcess();
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            TimeSpan? previousCpu = null;
            long previousAt = 0;
            try
            {
                while (await timer.WaitForNextTickAsync(samplingStop.Token))
                {
                    var now = Stopwatch.GetTimestamp();
                    if (Stopwatch.GetElapsedTime(Volatile.Read(ref lastNativeSample), now) < TimeSpan.FromSeconds(2)) continue;
                    process.Refresh();
                    var cpu = process.TotalProcessorTime;
                    var interval = previousCpu is null ? (double?)null : Stopwatch.GetElapsedTime(previousAt, now).TotalSeconds;
                    var point = new ResourcePoint(DateTimeOffset.UtcNow, interval,
                        previousCpu is { } previous && interval > 0 ? Math.Max(0, (cpu - previous).TotalSeconds / interval.Value * 100) : null,
                        checked((ulong)process.WorkingSet64), null, null, null, null, null, null,
                        "process", "process-rss", "unavailable",
                        interval is null ? "First host-owned sample; no CPU baseline." : null, null,
                        "Agent disconnected; host-only sampling cannot resume native network accounting.",
                        "Agent disconnected; native network rates unavailable.");
                    var observation = new ResourceObservation(point, null, "Agent disconnected; machine counters unavailable.");
                    spool!.Resource(new ResourceSample { Task = dispatch.Task.Clone(), ReservationEpoch = dispatch.ReservationEpoch,
                        SampleJson = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes(observation,
                            ResourceJsonContext.Default.ResourceObservation)) });
                    previousCpu = cpu;
                    previousAt = now;
                }
            }
            catch (OperationCanceledException) when (samplingStop.IsCancellationRequested) { }
        }

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
