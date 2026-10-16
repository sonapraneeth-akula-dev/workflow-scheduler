using System.Collections.Concurrent;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Wf.Agent.Secrets;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Logs;

namespace Wf.Agent.Logs;

/// <summary>Upload lifetime is independent of execution/result acknowledgement. A failed log subsystem
/// is reported explicitly without changing the task outcome; captured bytes remain for recovery.</summary>
public sealed class AgentLogService : IAsyncDisposable
{
    private readonly AgentOptions options;
    private readonly TimeProvider clock;
    private readonly ILogger<AgentLogService> logger;
    private readonly HttpClient http = new(new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(10) })
    {
        DefaultRequestVersion = System.Net.HttpVersion.Version20,
        DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
    };
    private readonly LogChunkUploader uploader;
    private readonly CancellationTokenSource stop = new();
    private readonly ConcurrentDictionary<string, Capture> captures = new();
    private Exception? lastFailure;
    private int disposed;

    public Exception? LastFailure => Volatile.Read(ref lastFailure);

    public AgentLogService(AgentOptions options, TimeProvider clock, ILogger<AgentLogService> logger,
        ILogger<LogChunkUploader> uploadLogger)
    {
        this.options = options; this.clock = clock; this.logger = logger;
        http.Timeout = TimeSpan.FromSeconds(30);
        uploader = new(http, clock, uploadLogger);
    }

    public async Task<Capture> BeginAsync(Dispatch dispatch)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var cap = dispatch.Limits?.MaxLogBytes is > 0
            ? (long)Math.Min(dispatch.Limits.MaxLogBytes, (ulong)options.MaxLogBytes) : options.MaxLogBytes;
        var spool = new LogSpooler(options.LogSpoolDirectory, dispatch.Task, new SecretMasker([]),
            new(MaxBytes: cap, SpoolQuotaBytes: options.LogSpoolQuotaBytes), clock);
        try
        {
            var grant = dispatch.Grants.SingleOrDefault(grant => grant.Kind == "logs");
            if (grant is not null)
                LogSpooler.DurableWrite(Path.Combine(spool.DirectoryPath, "upload-grant.pb"), grant.ToByteArray());
            return Track(spool, grant);
        }
        catch { await spool.DisposeAsync(); throw; }
    }

    public async Task RecoverAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (!Directory.Exists(options.LogSpoolDirectory)) return;
        LogSpooler.RejectLinks(options.LogSpoolDirectory);
        foreach (var path in LogSpooler.Files(options.LogSpoolDirectory, "index.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            LogSpooler.RejectLinks(path);
            var directory = Path.GetDirectoryName(path)!;
            if (File.Exists(Path.Combine(directory, "uploaded")))
            {
                LogSpooler.RejectLinks(Path.Combine(directory, "uploaded"));
                foreach (var chunk in Directory.EnumerateFiles(directory, "*.log.gz")) File.Delete(chunk);
                File.Delete(Path.Combine(directory, "upload-grant.pb"));
                continue;
            }
            var index = LogSpooler.ReadIndex(path);
            var attempt = new AttemptRef { RunId = index.RunId, NodeKey = index.NodeKey, TaskKey = index.TaskKey, Attempt = index.Attempt };
            var spool = new LogSpooler(options.LogSpoolDirectory, attempt, new SecretMasker([]),
                new(MaxBytes: options.MaxLogBytes, SpoolQuotaBytes: options.LogSpoolQuotaBytes), clock);
            if (!spool.IsComplete)
            {
                // Host socket re-adoption is a separate task. Never present an interrupted capture as complete.
                await spool.CompleteAsync(captureInterrupted: true);
                Report(new IOException("Task-host capture was interrupted; durable bytes recovered."));
            }
            var grantPath = Path.Combine(directory, "upload-grant.pb");
            var grant = File.Exists(grantPath) ? UploadGrant.Parser.ParseFrom(File.ReadAllBytes(grantPath)) : null;
            Track(spool, grant);
        }
    }

    private Capture Track(LogSpooler spool, UploadGrant? grant)
    {
        var capture = new Capture(spool, this);
        if (spool.IsComplete) capture.Closed.TrySetResult();
        if (!captures.TryAdd(spool.DirectoryPath, capture))
            throw new InvalidOperationException("Attempt log capture already exists.");
        capture.Upload = RunAsync();
        return capture;

        async Task RunAsync()
        {
            try
            {
                if (grant is null)
                {
                    Report(new InvalidDataException("Dispatch has no attempt log upload capability; local spool retained."));
                    return;
                }
                await uploader.UploadAsync(spool, grant, options.Engines[0], stop.Token);
                File.Delete(Path.Combine(spool.DirectoryPath, "upload-grant.pb"));
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or HttpRequestException or InvalidDataException or JsonException)
            { Report(error); }
            finally
            {
                try { await capture.Closed.Task.WaitAsync(stop.Token); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                try { await spool.DisposeAsync(); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Report(error); }
                captures.TryRemove(spool.DirectoryPath, out _);
            }
        }
    }

    public void Report(Exception error)
    {
        Volatile.Write(ref lastFailure, error);
        logger.LogCritical("Agent log capture/upload failed ({ErrorType}); inspect the durable spool and agent disk. Task outcome is unchanged.",
            error.GetType().Name);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        await stop.CancelAsync();
        foreach (var capture in captures.Values)
        {
            await capture.Upload;
            await capture.Spool.DisposeAsync();
        }
        stop.Dispose();
        http.Dispose();
    }

    public sealed class Capture(LogSpooler spool, AgentLogService owner)
    {
        internal LogSpooler Spool { get; } = spool;
        internal TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Upload { get; internal set; } = Task.CompletedTask;
        public Exception? Failure { get; private set; }

        public async Task WriteAsync(string stream, string text)
        {
            if (Failure is not null) return;
            try { await Spool.WriteAsync(stream, text, stream == "stderr" ? "Error" : "Information"); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Failure = error;
                owner.Report(error);
            }
        }

        public async Task CompleteAsync()
        {
            try { if (Failure is null) await Spool.CompleteAsync(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Failure = error;
                owner.Report(error);
            }
            finally { Closed.TrySetResult(); }
        }
    }
}
