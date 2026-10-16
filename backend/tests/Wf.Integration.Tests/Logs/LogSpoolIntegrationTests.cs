using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wf.Agent;
using Wf.Agent.Hosts;
using Wf.Agent.Journal;
using Wf.Agent.Logs;
using Wf.Agent.Packages;
using Wf.Agent.Secrets;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Logs;
using Wf.Contracts.Registration;
using Wf.ControlPlane.Storage;
using Wf.TaskHost.Fixture;

namespace Wf.Integration.Tests.Logs;

public sealed class LogSpoolIntegrationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_agent_executable_streams_task_logs_to_real_signed_HTTP2_storage(bool restartDuringUpload)
    {
        await using var server = await LogReceiver.StartAsync();
        var archive = await WorkflowArchiveAsync();
        server.App.MapGet("/package", () => Results.Bytes(archive));
        var dispatch = Dispatch("unicodeLogs");
        dispatch.PackageUri = new Uri(server.PackageAddress, "/package").ToString();
        dispatch.Sha256 = Convert.ToHexStringLower(SHA256.HashData(archive));
        dispatch.NodeOutputsJson = ByteString.CopyFromUtf8("""{"previous":0}""");
        var capability = server.Capabilities.Create(dispatch.Task, dispatch.DeadlineAt.ToDateTimeOffset());
        dispatch.Grants.Add(capability);
        server.Gateway.Dispatch = dispatch;
        if (restartDuringUpload) server.FailBeforeCommit = int.MaxValue;
        var executable = Environment.GetEnvironmentVariable("WF_AGENT_EXECUTABLE");
        if (executable is not null) Assert.True(File.Exists(executable), "Configured native agent is missing.");
        var start = new ProcessStartInfo(executable ?? "dotnet")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = server.Root
        };
        if (executable is null)
        {
            start.ArgumentList.Add("exec");
            start.ArgumentList.Add("--runtimeconfig");
            start.ArgumentList.Add(Path.ChangeExtension(typeof(LogSpoolIntegrationTests).Assembly.Location, ".runtimeconfig.json"));
            start.ArgumentList.Add("--depsfile");
            start.ArgumentList.Add(Path.ChangeExtension(typeof(LogSpoolIntegrationTests).Assembly.Location, ".deps.json"));
            start.ArgumentList.Add(typeof(AgentConnection).Assembly.Location);
        }
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("Agent__", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        start.Environment["Agent__Id"] = Guid.NewGuid().ToString();
        start.Environment["Agent__Name"] = "log-process-test";
        start.Environment["Agent__Engines__0"] = server.Address.ToString();
        start.Environment["Agent__ResultJournalDirectory"] = Path.Combine(server.Root, "results");
        start.Environment["Agent__PackageCache"] = Path.Combine(server.Root, "packages");
        start.Environment["Agent__LogSpoolDirectory"] = server.SpoolRoot;
        start.Environment["Agent__MaxTasks"] = "1";
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(Token);
        var stderr = process.StandardError.ReadToEndAsync(Token);
        var resumedDiagnostics = "";
        try
        {
            var completed = await server.Gateway.Completion.Task.WaitAsync(TimeSpan.FromSeconds(30), Token);
            Assert.True(completed.Outcome == TaskOutcome.Succeeded, $"Task failed: {completed.Error}");
            if (restartDuringUpload)
            {
                await server.FirstPut.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
                var directory = Path.Combine(server.SpoolRoot, LogIndex.PrefixFor(dispatch.Task).Replace('/', Path.DirectorySeparatorChar));
                Assert.Single(Directory.GetFiles(directory, "*.log.gz"));
                Assert.False(File.Exists(Path.Combine(directory, "uploaded")));
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(Token);
                await Task.WhenAll(stdout, stderr);
                server.FailBeforeCommit = 0;
                using var resumed = Process.Start(start)!;
                var resumedOut = resumed.StandardOutput.ReadToEndAsync(Token);
                var resumedError = resumed.StandardError.ReadToEndAsync(Token);
                try
                {
                    await UntilAsync(() => Task.FromResult(File.Exists(Path.Combine(directory, "uploaded"))));
                    Assert.False(resumed.HasExited);
                    Assert.Equal(1, server.Gateway.DispatchCount);
                }
                finally
                {
                    if (!resumed.HasExited) resumed.Kill(entireProcessTree: true);
                    await resumed.WaitForExitAsync(Token);
                    resumedDiagnostics = await resumedOut + await resumedError;
                }
            }
            await UntilAsync(() => Task.FromResult(File.Exists(server.IndexPath(dispatch.Task))));
            var index = server.ReadIndex(dispatch.Task);
            Assert.True(index.Complete);
            Assert.False(index.CaptureInterrupted);
            Assert.Equal(new[] { new string('x', 16383) + "\U0001F511", new string('y', 16384), "" },
                ReadFrames(server.BlobRoot, index).Select(frame => frame.Text));
            if (!restartDuringUpload) Assert.False(process.HasExited);
            using var output = JsonDocument.Parse(completed.OutputsJson.Memory);
            AssertStopped(output.RootElement.GetProperty("pid").GetInt32());
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(Token);
            await Task.WhenAll(stdout, stderr);
        }
        var token = capability.UploadUri.Split('/')[^1];
        Assert.True(!(await stdout + await stderr + resumedDiagnostics).Contains(token, StringComparison.Ordinal),
            "Agent diagnostics exposed an upload capability.");
    }

    [Fact]
    public async Task Real_task_stdout_stderr_ctx_and_unterminated_line_are_masked_before_disk_and_HTTP_upload()
    {
        await using var server = await LogReceiver.StartAsync();
        var dispatch = Dispatch("logs");
        dispatch.InputsJson = ByteString.CopyFromUtf8("""{"source":{"value":"secret-token"}}""");
        await using var spool = new LogSpooler(server.SpoolRoot, dispatch.Task, new SecretMasker([]));
        await using var hosts = new TaskHostPool(NullLogger<TaskHostPool>.Instance,
            containmentDirectory: Path.Combine(server.Root, "host-lifetimes"));
        var result = await hosts.ExecuteAsync(Package(), dispatch, cancellationToken: Token,
            secretMasker: new SecretMasker(["secret-token"]),
            captureLog: (stream, text) => spool.WriteAsync(stream, text,
                stream == "stderr" ? "Error" : "Information", Token));
        Assert.Equal(TaskOutcome.Succeeded, result.Outcome);
        await spool.CompleteAsync();
        var local = await spool.SnapshotAsync(Token);
        var localFrames = ReadFrames(server.SpoolRoot, local);
        Assert.Equal(4, localFrames.Length);
        Assert.Contains(localFrames, frame => frame.Stream == "stdout" && frame.Text == "stdout *** tail");
        Assert.Contains(localFrames, frame => frame.Stream == "stderr" && frame.Level == "Error" && frame.Text == "stderr ***");
        Assert.Contains(localFrames, frame => frame.Text == "ctx ***");
        Assert.Contains(localFrames, frame => frame.Text == "unterminated");
        Assert.DoesNotContain("secret-token", string.Join('\n', localFrames.Select(frame => frame.Text)));
        Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes("secret-token")),
            string.Join('\n', localFrames.Select(frame => frame.Text)));
        await server.Uploader.UploadAsync(spool, server.Capabilities.Create(dispatch.Task, DateTimeOffset.UtcNow.AddHours(1)),
            server.Address, Token);
        var index = server.ReadIndex(dispatch.Task);
        Assert.Equal(local.Bytes, index.Bytes);
        Assert.Equal(4, index.Lines);
        Assert.Equal(localFrames.OrderBy(frame => frame.Text), ReadFrames(server.BlobRoot, index).OrderBy(frame => frame.Text));
        Assert.True(spool.IsUploaded);
        Assert.Empty(Directory.GetFiles(spool.DirectoryPath, "*.log.gz"));
        using var outputs = JsonDocument.Parse(result.OutputsJson.Memory);
        AssertStopped(outputs.RootElement.GetProperty("pid").GetInt32());
        Assert.Equal(1, hosts.ProcessStartCount);
        Assert.Empty(hosts.WarmProcessIds);
        // The actual signed-upload endpoints have no PostgreSQL service or log-body persistence path.
        Assert.Null(server.App.Services.GetService<Npgsql.NpgsqlDataSource>());
    }

    [Fact]
    public async Task Real_pipe_capture_preserves_unicode_at_fragment_boundary_CRLF_and_actual_blank_lines()
    {
        await using var server = await LogReceiver.StartAsync();
        var dispatch = Dispatch("unicodeLogs");
        await using var spool = new LogSpooler(server.SpoolRoot, dispatch.Task, new SecretMasker([]));
        await using var hosts = new TaskHostPool(NullLogger<TaskHostPool>.Instance,
            containmentDirectory: Path.Combine(server.Root, "host-lifetimes"));
        var result = await hosts.ExecuteAsync(Package(), dispatch, cancellationToken: Token,
            captureLog: (stream, text) => spool.WriteAsync(stream, text, cancellationToken: Token));
        Assert.Equal(TaskOutcome.Succeeded, result.Outcome);
        await spool.CompleteAsync();
        var frames = ReadFrames(server.SpoolRoot, await spool.SnapshotAsync(Token));
        Assert.Equal(new[] { new string('x', 16383) + "\U0001F511", new string('y', 16384), "" }, frames.Select(frame => frame.Text));
    }

    [Fact]
    public async Task Actual_TaskCommands_package_download_execution_and_background_upload_complete_end_to_end()
    {
        await using var server = await LogReceiver.StartAsync();
        var archive = await WorkflowArchiveAsync();
        server.App.MapGet("/package", () => Results.Bytes(archive));
        var dispatch = Dispatch("echo");
        dispatch.PackageUri = new Uri(server.PackageAddress, "/package").ToString();
        dispatch.Sha256 = Convert.ToHexStringLower(SHA256.HashData(archive));
        dispatch.Grants.Add(server.Capabilities.Create(dispatch.Task, dispatch.DeadlineAt.ToDateTimeOffset()));
        dispatch.NodeOutputsJson = ByteString.CopyFromUtf8("""{"previous":3}""");
        var options = new AgentOptions([server.Address], Guid.NewGuid().ToString(), "log-integration", "ci", 1,
            resultJournalDirectory: Path.Combine(server.Root, "results"), logSpoolDirectory: server.SpoolRoot);
        using var cache = new PackageCache(Path.Combine(server.Root, "packages"), NullLogger<PackageCache>.Instance);
        await using var hosts = new TaskHostPool(NullLogger<TaskHostPool>.Instance, options: options);
        await using var logs = new AgentLogService(options, TimeProvider.System, NullLogger<AgentLogService>.Instance,
            NullLogger<LogChunkUploader>.Instance);
        using var outbox = new AgentOutbox(new ResultJournal(options.ResultJournalDirectory, options.Id));
        await using var commands = new TaskCommands(options, cache, hosts, outbox, TimeProvider.System,
            NullLogger<TaskCommands>.Instance, logs);
        await commands.HandleAsync(new ControlMessage { Dispatch = dispatch }, Token);
        await UntilAsync(() => Task.FromResult(Completion(outbox) is not null));
        Assert.True(Completion(outbox)!.Outcome == TaskOutcome.Succeeded, $"Task failed: {Completion(outbox)!.Error}");
        await UntilAsync(() => Task.FromResult(File.Exists(server.IndexPath(dispatch.Task))));
        var index = server.ReadIndex(dispatch.Task);
        Assert.True(index.Complete);
        Assert.False(index.CaptureInterrupted);
        Assert.Equal("Executed actual SDK task delegate.", Assert.Single(ReadFrames(server.BlobRoot, index)).Text);
        await commands.DisposeAsync();
        Assert.Empty(commands.Inflight);
        Assert.Null(logs.LastFailure);
    }

    [Fact]
    public async Task Concurrent_attempts_share_quota_scan_and_cleanup_without_cross_identity_or_missing_file_races()
    {
        await using var server = await LogReceiver.StartAsync();
        var run = Guid.NewGuid().ToString();
        var attempts = Enumerable.Range(0, 8).Select(number => new AttemptRef
        {
            RunId = run, NodeKey = "node-" + number, TaskKey = "same-task", Attempt = 1
        }).ToArray();
        await Task.WhenAll(attempts.Select(attempt => Task.Run(async () =>
        {
            await using var spool = new LogSpooler(server.SpoolRoot, attempt, new SecretMasker([]));
            await spool.WriteAsync("stdout", attempt.NodeKey + new string('x', 32768), cancellationToken: Token);
            await spool.CompleteAsync();
            await server.Uploader.UploadAsync(spool, server.Capabilities.Create(attempt, DateTimeOffset.UtcNow.AddHours(1)),
                server.Address, Token);
            Assert.True(spool.IsUploaded);
        }, Token)));
        foreach (var attempt in attempts)
        {
            var index = server.ReadIndex(attempt);
            Assert.Equal(attempt.NodeKey, index.NodeKey);
            Assert.Equal(LogIndex.PrefixFor(attempt), index.Prefix);
            Assert.Equal(attempt.NodeKey + new string('x', 32768), Assert.Single(ReadFrames(server.BlobRoot, index)).Text);
        }
        Assert.Equal(8, Directory.GetFiles(server.BlobRoot, "index.json", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public async Task Real_HTTP_transient_failure_and_lost_commit_ack_retry_without_deleting_or_duplicating_chunks()
    {
        await using var server = await LogReceiver.StartAsync();
        server.FailBeforeCommit = 1;
        server.LoseChunkAcknowledgements = 1;
        server.LoseIndexAcknowledgements = 1;
        server.BlockFirstPut = true;
        var attempt = Attempt();
        await using var spool = new LogSpooler(server.SpoolRoot, attempt, new SecretMasker([]));
        await spool.WriteAsync("stdout", "durable retry payload", cancellationToken: Token);
        await spool.CompleteAsync();
        var before = await spool.SnapshotAsync(Token);
        var chunk = Assert.Single(before.Chunks);
        var path = Path.Combine(spool.DirectoryPath, chunk.Name);
        var upload = server.Uploader.UploadAsync(spool,
            server.Capabilities.Create(attempt, DateTimeOffset.UtcNow.AddHours(1)), server.Address, Token);
        await server.FirstPut.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.True(File.Exists(path));
        Assert.False(spool.IsUploaded);
        Assert.False(Assert.Single((await spool.SnapshotAsync(Token)).Chunks).Uploaded);
        server.ReleasePut.TrySetResult();
        await upload.WaitAsync(TimeSpan.FromSeconds(30), Token);
        var final = server.ReadIndex(attempt);
        Assert.True(spool.IsUploaded);
        Assert.False(File.Exists(path));
        Assert.Equal(before.Bytes, final.Bytes);
        Assert.Equal(1, final.Lines);
        Assert.Equal("durable retry payload", Assert.Single(ReadFrames(server.BlobRoot, final)).Text);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(server.IndexPath(attempt))!, "*.log.gz"));
        Assert.Equal(3, server.ChunkPuts);
        Assert.Equal(2, server.IndexPuts);
    }

    [Fact]
    public async Task Service_restart_replays_real_spool_and_grant_after_storage_outage()
    {
        await using var server = await LogReceiver.StartAsync();
        server.FailBeforeCommit = int.MaxValue;
        var dispatch = Dispatch("logs");
        dispatch.Grants.Add(server.Capabilities.Create(dispatch.Task, DateTimeOffset.UtcNow.AddHours(1)));
        var options = new AgentOptions([server.Address], Guid.NewGuid().ToString(), "restart-log-agent", "ci", 1,
            resultJournalDirectory: Path.Combine(server.Root, "results"), logSpoolDirectory: server.SpoolRoot);
        await using (var first = new AgentLogService(options, TimeProvider.System, NullLogger<AgentLogService>.Instance,
            NullLogger<LogChunkUploader>.Instance))
        {
            var capture = await first.BeginAsync(dispatch);
            await capture.WriteAsync("stdout", "survives restart");
            await capture.CompleteAsync();
            await server.FirstPut.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        }
        var directory = Path.Combine(server.SpoolRoot, LogIndex.PrefixFor(dispatch.Task).Replace('/', Path.DirectorySeparatorChar));
        Assert.Single(Directory.GetFiles(directory, "*.log.gz"));
        Assert.False(File.Exists(Path.Combine(directory, "uploaded")));
        server.FailBeforeCommit = 0;
        await using var restarted = new AgentLogService(options, TimeProvider.System, NullLogger<AgentLogService>.Instance,
            NullLogger<LogChunkUploader>.Instance);
        await restarted.RecoverAsync(Token);
        await UntilAsync(() => Task.FromResult(File.Exists(Path.Combine(directory, "uploaded"))));
        Assert.Equal("survives restart", Assert.Single(ReadFrames(server.BlobRoot, server.ReadIndex(dispatch.Task))).Text);
        Assert.Empty(Directory.GetFiles(directory, "*.log.gz"));
        await UntilAsync(() => Task.FromResult(!File.Exists(Path.Combine(directory, "upload-grant.pb"))));
        Assert.Null(restarted.LastFailure);
    }

    [Fact]
    public async Task Interrupted_capture_recovers_committed_bytes_but_never_claims_continuous_live_host_adoption()
    {
        await using var server = await LogReceiver.StartAsync();
        var dispatch = Dispatch("logs");
        dispatch.Grants.Add(server.Capabilities.Create(dispatch.Task, DateTimeOffset.UtcNow.AddHours(1)));
        var options = new AgentOptions([server.Address], Guid.NewGuid().ToString(), "interrupted", "ci", 1,
            resultJournalDirectory: Path.Combine(server.Root, "results"), logSpoolDirectory: server.SpoolRoot);
        await using (var first = new AgentLogService(options, TimeProvider.System, NullLogger<AgentLogService>.Instance,
            NullLogger<LogChunkUploader>.Instance))
        {
            var capture = await first.BeginAsync(dispatch);
            await capture.WriteAsync("stdout", "committed before restart");
        }
        await using var restarted = new AgentLogService(options, TimeProvider.System, NullLogger<AgentLogService>.Instance,
            NullLogger<LogChunkUploader>.Instance);
        await restarted.RecoverAsync(Token);
        await UntilAsync(() => Task.FromResult(File.Exists(server.IndexPath(dispatch.Task))));
        var index = server.ReadIndex(dispatch.Task);
        Assert.True(index.Complete);
        Assert.True(index.CaptureInterrupted);
        Assert.IsType<IOException>(restarted.LastFailure);
        Assert.Equal("committed before restart", Assert.Single(ReadFrames(server.BlobRoot, index)).Text);
    }

    [Fact]
    public async Task Capture_disk_failure_is_explicit_does_not_fail_task_and_does_not_deadlock_its_pipes()
    {
        await using var server = await LogReceiver.StartAsync();
        var dispatch = Dispatch("echo");
        var options = new AgentOptions([server.Address], Guid.NewGuid().ToString(), "bad-disk", "ci", 1,
            resultJournalDirectory: Path.Combine(server.Root, "results"), logSpoolDirectory: server.SpoolRoot);
        await using var logs = new AgentLogService(options, TimeProvider.System, NullLogger<AgentLogService>.Instance,
            NullLogger<LogChunkUploader>.Instance);
        var capture = await logs.BeginAsync(dispatch);
        var directory = Path.Combine(server.SpoolRoot, LogIndex.PrefixFor(dispatch.Task).Replace('/', Path.DirectorySeparatorChar));
        var obstruction = Path.Combine(directory, "index.json.pending");
        Directory.CreateDirectory(obstruction);
        await using var hosts = new TaskHostPool(NullLogger<TaskHostPool>.Instance, options: options);
        var result = await hosts.ExecuteAsync(Package(), dispatch, ByteString.CopyFromUtf8("""{"previous":0}"""),
            cancellationToken: Token, captureLog: capture.WriteAsync);
        Assert.Equal(TaskOutcome.Succeeded, result.Outcome);
        Assert.True(capture.Failure is IOException or UnauthorizedAccessException);
        Assert.Same(capture.Failure, logs.LastFailure);
        await capture.CompleteAsync();
        Directory.Delete(obstruction);
        using var outputs = JsonDocument.Parse(result.OutputsJson.Memory);
        AssertStopped(outputs.RootElement.GetProperty("pid").GetInt32());
    }

    [Fact]
    public async Task Real_signed_endpoints_reject_cross_attempt_names_expiry_tampering_bad_hash_and_overwrite()
    {
        await using var server = await LogReceiver.StartAsync();
        var attempt = Attempt();
        var cap = server.Capabilities.Create(attempt, DateTimeOffset.UtcNow.AddHours(1));
        var bytes = Encoding.UTF8.GetBytes("immutable content");
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        foreach (var name in new[] { "../index.json", "other/000000.log.gz", "\\escape", "/absolute", "000000.log.gz?x" })
        {
            using var invalid = await server.Http.PostAsJsonAsync(new Uri(server.Address, cap.UploadUri),
                new LogUploadRequest(name, hash, bytes.Length), LogJsonContext.Default.LogUploadRequest, Token);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        using (var tampered = await server.Http.PostAsJsonAsync(new Uri(server.Address, cap.UploadUri + "x"),
            new LogUploadRequest("000000.log.gz", hash, bytes.Length), LogJsonContext.Default.LogUploadRequest, Token))
            Assert.Equal(HttpStatusCode.Forbidden, tampered.StatusCode);
        var expired = server.Capabilities.Create(attempt, DateTimeOffset.UtcNow.AddDays(-2));
        using (var rejected = await server.Http.PostAsJsonAsync(new Uri(server.Address, expired.UploadUri),
            new LogUploadRequest("000000.log.gz", hash, bytes.Length), LogJsonContext.Default.LogUploadRequest, Token))
            Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        var grant = await server.GrantAsync(cap, "000000.log.gz", bytes);
        using (var bad = await server.Http.PutAsync(new Uri(server.Address, grant.UploadUri),
            new ByteArrayContent(Encoding.UTF8.GetBytes("wrong content!!!")), Token))
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        using (var good = await server.Http.PutAsync(new Uri(server.Address, grant.UploadUri), new ByteArrayContent(bytes), Token))
            Assert.Equal(HttpStatusCode.Created, good.StatusCode);
        var replacement = Encoding.UTF8.GetBytes("different content");
        var replacementGrant = await server.GrantAsync(cap, "000000.log.gz", replacement);
        using (var overwrite = await server.Http.PutAsync(new Uri(server.Address, replacementGrant.UploadUri), new ByteArrayContent(replacement), Token))
            Assert.Equal(HttpStatusCode.BadRequest, overwrite.StatusCode);
        var target = Path.Combine(server.BlobRoot, LogIndex.PrefixFor(attempt).Replace('/', Path.DirectorySeparatorChar), "000000.log.gz");
        Assert.Equal(bytes, File.ReadAllBytes(target));
        Assert.Empty(Directory.GetFiles(server.BlobRoot, ".upload-*"));
    }

    [Fact]
    public async Task Permanent_upload_denial_is_visible_and_preserves_real_local_chunk_for_recovery()
    {
        await using var server = await LogReceiver.StartAsync();
        server.DenyUploads = true;
        var attempt = Attempt();
        await using var spool = new LogSpooler(server.SpoolRoot, attempt, new SecretMasker([]));
        await spool.WriteAsync("stdout", "not lost on 403", cancellationToken: Token);
        await spool.CompleteAsync();
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => server.Uploader.UploadAsync(spool,
            server.Capabilities.Create(attempt, DateTimeOffset.UtcNow.AddHours(1)), server.Address, Token));
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.False(spool.IsUploaded);
        Assert.Single(Directory.GetFiles(spool.DirectoryPath, "*.log.gz"));
        Assert.False(Assert.Single((await spool.SnapshotAsync(Token)).Chunks).Uploaded);
    }

    private static TaskHostPackage Package() => new("1.0.0", "dotnet",
        ["exec", "--runtimeconfig", Path.ChangeExtension(typeof(LogSpoolIntegrationTests).Assembly.Location, ".runtimeconfig.json"),
            "--depsfile", Path.ChangeExtension(typeof(LogSpoolIntegrationTests).Assembly.Location, ".deps.json"), typeof(FixtureMarker).Assembly.Location],
        AppContext.BaseDirectory);

    private static AttemptRef Attempt() => new() { RunId = Guid.NewGuid().ToString(), NodeKey = "work", TaskKey = "logs", Attempt = 1 };
    private static Dispatch Dispatch(string task) => new()
    {
        Task = new AttemptRef { RunId = Guid.NewGuid().ToString(), NodeKey = "work", TaskKey = task, Attempt = 1 },
        Version = "1.0.0", ReservationEpoch = 1,
        DeadlineAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(1)),
        InputsJson = ByteString.CopyFromUtf8("""{"source":{"value":7}}"""),
        VariablesJson = ByteString.CopyFromUtf8("""{"amount":5}""")
    };

    private static TaskCompleted? Completion(AgentOutbox outbox)
    {
        ulong sequence = 0;
        while (outbox.NextAfter(sequence) is { } message)
        {
            sequence = message.Sequence;
            if (message.TaskCompleted is not null) return message.TaskCompleted;
        }
        return null;
    }

    internal static LogFrame[] ReadFrames(string root, LogIndex index)
    {
        using var output = new MemoryStream();
        foreach (var chunk in index.Chunks)
        {
            var path = Path.Combine(root, index.Prefix.Replace('/', Path.DirectorySeparatorChar), chunk.Name);
            using var gzip = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
            using var bytes = new MemoryStream();
            gzip.CopyTo(bytes);
            output.Write(bytes.GetBuffer(), (int)(chunk.KeepStart - chunk.ByteStart), (int)(chunk.KeepEnd - chunk.KeepStart));
        }
        return Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonSerializer.Deserialize(line, LogJsonContext.Default.LogFrame)!).ToArray();
    }

    private static void AssertStopped(int pid)
    {
        try { using var process = Process.GetProcessById(pid); Assert.True(process.HasExited); }
        catch (ArgumentException) { }
    }

    internal static async Task UntilAsync(Func<Task<bool>> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        while (!await condition()) await Task.Delay(20, timeout.Token);
    }

    private static async Task<byte[]> WorkflowArchiveAsync()
    {
        using var bytes = new MemoryStream();
        using (var archive = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var path in Directory.EnumerateFiles(AppContext.BaseDirectory).Where(path => Path.GetExtension(path) is ".dll" or ".json"))
            {
                await using var input = File.OpenRead(path);
                await using var output = archive.CreateEntry(Path.GetFileName(path)).Open();
                await input.CopyToAsync(output, Token);
            }
            await using var writer = new StreamWriter(archive.CreateEntry("wf-package.json").Open(), Encoding.UTF8);
            await writer.WriteAsync($$"""{"version":"1.0.0","entryAssembly":"{{Path.GetFileName(typeof(FixtureMarker).Assembly.Location)}}"}""".AsMemory(), Token);
        }
        return bytes.ToArray();
    }
}

internal sealed class LogReceiver : IAsyncDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "wf-log-" + Guid.NewGuid().ToString("N")[..12]);
    public string SpoolRoot => Path.Combine(Root, "spool");
    public string BlobRoot => Path.Combine(Root, "blobs");
    public WebApplication App { get; private set; } = null!;
    public Uri Address { get; private set; } = null!;
    public Uri PackageAddress { get; private set; } = null!;
    public HttpClient Http { get; } = Client();
    public LogUploadCapabilities Capabilities { get; private set; } = null!;
    public LogSpoolGatewayState Gateway { get; } = new();
    public LogChunkUploader Uploader => new(Http, TimeProvider.System, NullLogger<LogChunkUploader>.Instance);
    public int FailBeforeCommit { get; set; }
    public int LoseChunkAcknowledgements { get; set; }
    public int LoseIndexAcknowledgements { get; set; }
    public bool DenyUploads { get; set; }
    public bool BlockFirstPut { get; set; }
    public int ChunkPuts { get; private set; }
    public int IndexPuts { get; private set; }
    public TaskCompletionSource FirstPut { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleasePut { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static async Task<LogReceiver> StartAsync()
    {
        var server = new LogReceiver();
        Directory.CreateDirectory(server.Root);
        var key = RandomNumberGenerator.GetBytes(32);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http2);
            options.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http1);
        });
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(server.Gateway);
        builder.Services.AddSingleton(new LogUploadCapabilities(key, TimeProvider.System));
        builder.Services.AddSingleton(new BlobUploadGrants(key));
        builder.Services.AddSingleton<IBlobStore>(new FileSystemBlobStore(server.BlobRoot, key));
        server.App = builder.Build();
        server.Capabilities = server.App.Services.GetRequiredService<LogUploadCapabilities>();
        server.App.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments("/api/v1/log-uploads")) { await next(context); return; }
            if (server.DenyUploads) { context.Response.StatusCode = 403; return; }
            var grant = context.Request.Path.Value!.Split('/')[^1];
            var content = server.App.Services.GetRequiredService<BlobUploadGrants>().Read(grant);
            var index = content.Key.EndsWith("index.json", StringComparison.Ordinal);
            if (index) server.IndexPuts++; else server.ChunkPuts++;
            if (server.FirstPut.TrySetResult() && server.BlockFirstPut)
                await server.ReleasePut.Task.WaitAsync(TestContext.Current.CancellationToken);
            if (server.FailBeforeCommit > 0) { server.FailBeforeCommit--; context.Response.StatusCode = 503; return; }
            await next(context);
            if (index && server.LoseIndexAcknowledgements > 0) { server.LoseIndexAcknowledgements--; context.Response.StatusCode = 503; }
            if (!index && server.LoseChunkAcknowledgements > 0) { server.LoseChunkAcknowledgements--; context.Response.StatusCode = 503; }
        });
        server.App.MapLogUploadEndpoints();
        server.App.MapGrpcService<LogSpoolGateway>();
        await server.App.StartAsync(TestContext.Current.CancellationToken);
        server.Address = new Uri(server.App.Urls.First());
        server.PackageAddress = new Uri(server.App.Urls.Last());
        return server;
    }

    public sealed class LogSpoolGatewayState
    {
        private int dispatched;
        public Dispatch? Dispatch { get; set; }
        public int DispatchCount => Volatile.Read(ref dispatched);
        public bool TryDispatch() => Interlocked.CompareExchange(ref dispatched, 1, 0) == 0;
        public TaskCompletionSource<TaskCompleted> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed class LogSpoolGateway(LogSpoolGatewayState state) : AgentGateway.AgentGatewayBase
    {
        public override async Task Connect(IAsyncStreamReader<AgentMessage> requests,
            IServerStreamWriter<ControlMessage> responses, ServerCallContext context)
        {
            if (!await requests.MoveNext(context.CancellationToken)) return;
            if (requests.Current.Hello is null) throw new RpcException(new Status(StatusCode.InvalidArgument, "Hello required."));
            ulong sequence = 1;
            await responses.WriteAsync(new ControlMessage
            {
                Sequence = sequence++, LeaderEpoch = 1, Ack = new Ack { Sequence = requests.Current.Sequence }
            }, context.CancellationToken);
            if (state.TryDispatch())
                await responses.WriteAsync(new ControlMessage
                {
                    Sequence = sequence++, LeaderEpoch = 1, Dispatch = state.Dispatch!.Clone()
                }, context.CancellationToken);
            await foreach (var message in requests.ReadAllAsync(context.CancellationToken))
            {
                if (message.TaskCompleted is { } completed) state.Completion.TrySetResult(completed.Clone());
                if (message.Ack is null)
                    await responses.WriteAsync(new ControlMessage
                    {
                        Sequence = sequence++, LeaderEpoch = 1, Ack = new Ack { Sequence = message.Sequence }
                    }, context.CancellationToken);
            }
        }
    }

    public static HttpClient Client() => new(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        DefaultRequestVersion = HttpVersion.Version20, DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        Timeout = TimeSpan.FromSeconds(10)
    };

    public string IndexPath(AttemptRef attempt) =>
        Path.Combine(BlobRoot, LogIndex.PrefixFor(attempt).Replace('/', Path.DirectorySeparatorChar), "index.json");

    public LogIndex ReadIndex(AttemptRef attempt) => JsonSerializer.Deserialize(File.ReadAllBytes(IndexPath(attempt)), LogJsonContext.Default.LogIndex)!;

    public async Task<UploadGrantResponse> GrantAsync(UploadGrant cap, string name, byte[] bytes)
    {
        using var response = await Http.PostAsJsonAsync(new Uri(Address, cap.UploadUri),
            new LogUploadRequest(name, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length),
            LogJsonContext.Default.LogUploadRequest, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync(RegistrationJsonContext.Default.UploadGrantResponse, TestContext.Current.CancellationToken))!;
    }

    public async ValueTask DisposeAsync()
    {
        ReleasePut.TrySetResult();
        await App.DisposeAsync();
        Http.Dispose();
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }
}
