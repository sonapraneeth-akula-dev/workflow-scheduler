using System.Collections.Immutable;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Wf.Contracts;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Definition;
using Wf.Contracts.Events;
using Wf.Contracts.Logs;
using Wf.Contracts.Registry;
using Wf.ControlPlane.Api;
using Wf.ControlPlane.Engine.Agents;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Live;
using Wf.ControlPlane.Storage;
using Wf.Engine.Core;

namespace Wf.Integration.Tests.Auth;

public sealed partial class AuthorizationMatrixTests
{
    [Fact]
    [Trait("Category", "FrontendBrowser")]
    public async Task Logs_Chrome_trace_opens_one_million_actual_archived_lines_under_one_second_and_scrolls_at_60fps()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("WF_LOGS_BROWSER") == "true",
            "Set WF_LOGS_BROWSER=true and WF_FRONTEND_ROOT with frontend dependencies and Chromium installed.");
        var frontend = Environment.GetEnvironmentVariable("WF_FRONTEND_ROOT");
        Assert.True(frontend is not null && File.Exists(Path.Combine(frontend, "scripts", "task-logs-browser.ts")));
        var group = Guid.NewGuid();
        var seed = await Seed(RegistryRole.Viewer, group, false);
        var workflow = seed.Type + "/logs";
        await CatalogWorkflow(workflow);
        Guid version;
        await using (var query = database.DataSource.CreateCommand("SELECT latest_version_id FROM workflows WHERE id=$1"))
        {
            query.Parameters.AddWithValue(workflow);
            version = (Guid)(await query.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
        }
        var run = new RunState(Guid.CreateVersion7(), [new NodeDefinition("work", [], [new TaskDefinition("task")])],
            new WorkflowMetadata("release", ImmutableSortedDictionary<string, string>.Empty), _ => { });
        var store = new RunStore(database.DataSource, NullLogger<RunStore>.Instance);
        var token = TestContext.Current.CancellationToken;
        await store.CreateAsync(run, workflow, version, DateTimeOffset.UtcNow.AddHours(1), token);
        await store.AppendAsync(run, EventEntity.Run, null, (int)RunStatus.Running, DateTimeOffset.UtcNow, token);
        foreach (var status in new[] { ExecutionStatus.Ready, ExecutionStatus.Dispatched, ExecutionStatus.Running,
            ExecutionStatus.Retrying, ExecutionStatus.Dispatched, ExecutionStatus.Running })
            await store.AppendAsync(run, EventEntity.Task, "work/task", (int)status, DateTimeOffset.UtcNow, token);
        var artifacts = Path.GetFullPath(Path.Combine(frontend!, "..", ".agent-artifacts", "task-logs-" + Guid.NewGuid().ToString("N")));
        var blobRoot = Path.Combine(artifacts, "blobs");
        using var blobs = new FileSystemBlobStore(blobRoot, RandomNumberGenerator.GetBytes(32));
        var archived = new AttemptRef { RunId = run.Snapshot.RunId.ToString("D"), NodeKey = "work", TaskKey = "task", Attempt = 1 };
        await MillionLineArchive(blobs, archived, token);
        var active = archived.Clone();
        active.Attempt = 2;
        AgentSession? session = null;
        var ordinal = 0UL;
        await using var server = await Server.Start(database.DataSource, live: true,
            configureServices: services => { services.AddSingleton<IBlobStore>(blobs); services.AddWorkflowLiveLogs(); },
            configure: app =>
            {
                app.MapTaskLogsEndpoints();
                app.MapRunTimelineEndpoints();
                app.MapReservationQueueEndpoints();
                app.MapPost("/api/test/logs/emit", async (HttpContext context) =>
                {
                    if (session is not { } connected) return Results.StatusCode(503);
                    var relay = app.Services.GetRequiredService<LiveLogRelay>();
                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                    wait.CancelAfter(TimeSpan.FromSeconds(3));
                    while (!(await relay.PendingAsync(connected, new Dictionary<string, MirrorLogs>(), wait.Token)).Any(command => command.On))
                        await Task.Delay(10, wait.Token);
                    var at = DateTimeOffset.UtcNow;
                    var line = ++ordinal;
                    await app.Services.GetRequiredService<LiveLogSink>().AcceptAsync(connected, new()
                    {
                        Task = active, ReservationEpoch = 1,
                        Records = { new LiveLogRecord { Line = line, At = Timestamp.FromDateTimeOffset(at),
                            Stream = "stdout", Level = "Information", Text = $"live-browser-{line} *** <script>never execute</script>" } }
                    }, context.RequestAborted);
                    return Results.Ok(new { at, line });
                }).RequireAuthorization();
            });
        var id = Guid.NewGuid();
        var registered = await server.App.Services.GetRequiredService<AgentRegistry>().RegisterAsync(new Hello
        {
            Id = id.ToString(), Name = $"logs-browser-{id:N}", Os = "linux", Arch = "x64",
            Cores = 1, RamMb = 1024, MaxTasks = 1, Pool = "ci", Version = "1.0", ProtocolVersion = 1,
            SupportsLogMirroring = true
        }, token);
        session = registered;
        await using (var assign = database.DataSource.CreateCommand("""
            INSERT INTO task_attempts(run_day,run_id,task_key,attempt,status,deadline_at)
            VALUES (uuid_v7_run_day(@run),@run,'work/task',1,7,now()+interval '1 hour')
            ON CONFLICT DO NOTHING;
            INSERT INTO task_attempts(run_day,run_id,task_key,attempt,status,deadline_at,agent_id,
                dispatch_payload,placement_request,execution_policy,reservation_epoch)
            VALUES (uuid_v7_run_day(@run),@run,'work/task',2,3,now()+interval '1 hour',@agent,@dispatch,'{}','{}',1)
            ON CONFLICT (run_day,run_id,task_key,attempt) DO UPDATE SET agent_id=EXCLUDED.agent_id,
                dispatch_payload=EXCLUDED.dispatch_payload,placement_request='{}',execution_policy='{}',reservation_epoch=1
            """))
        {
            assign.Parameters.AddWithValue("run", run.Snapshot.RunId);
            assign.Parameters.AddWithValue("agent", registered.AgentId);
            assign.Parameters.AddWithValue("dispatch", new Dispatch { Task = active, ReservationEpoch = 1,
                DeadlineAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddHours(1)) }.ToByteArray());
            await assign.ExecuteNonQueryAsync(token);
        }
        var start = new ProcessStartInfo("bun")
        {
            WorkingDirectory = frontend, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(Path.Combine("scripts", "task-logs-browser.ts"));
        start.Environment["WF_LOGS_API"] = server.Api;
        start.Environment["WF_LOGS_TOKEN"] = server.Token(group: group);
        start.Environment["WF_LOGS_RUN"] = archived.RunId;
        start.Environment["WF_LOGS_ARTIFACTS"] = artifacts;
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(token);
        var stderr = process.StandardError.ReadToEndAsync(token);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromMinutes(4));
            await process.WaitForExitAsync(deadline.Token);
            Assert.True(process.ExitCode == 0, $"Actual log Chrome acceptance failed.\n{await stdout}\n{await stderr}");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
            Directory.Delete(blobRoot, recursive: true);
        }
    }

    private static async Task MillionLineArchive(IBlobStore blobs, AttemptRef task, CancellationToken token)
    {
        var prefix = LogIndex.PrefixFor(task);
        var chunks = new List<LogChunk>();
        var at = DateTimeOffset.UtcNow;
        using var raw = new MemoryStream();
        long bytes = 0, firstLine = 1, lastLine = 0;
        for (var line = 1; line <= 1_000_000; line++)
        {
            var frame = JsonSerializer.SerializeToUtf8Bytes(new LogFrame(at, "stdout", "Information",
                $"record {line:D7} masked *** <script>never execute</script>"), LogJsonContext.Default.LogFrame);
            if (raw.Length + frame.Length + 1 > TaskLogReader.MaximumChunkBytes) await Seal();
            raw.Write(frame);
            raw.WriteByte((byte)'\n');
            lastLine = line;
        }
        await Seal();
        var index = new LogIndex(1, task.RunId, task.NodeKey, task.TaskKey, task.Attempt, prefix,
            bytes, 1_000_000, false, 0, true, chunks, NextChunk: chunks.Count, SealedBytes: bytes, SealedLines: 1_000_000);
        TaskLogReader.Validate(index, task);
        var json = JsonSerializer.SerializeToUtf8Bytes(index, LogJsonContext.Default.LogIndex);
        await blobs.UploadAsync(new(BlobKind.Logs, Convert.ToHexStringLower(SHA256.HashData(json)), json.Length, prefix + "index.json"),
            new MemoryStream(json), token);

        async Task Seal()
        {
            if (raw.Length == 0) return;
            using var compressed = new MemoryStream();
            using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
                gzip.Write(raw.GetBuffer().AsSpan(0, checked((int)raw.Length)));
            var sha = Convert.ToHexStringLower(SHA256.HashData(compressed.GetBuffer().AsSpan(0, checked((int)compressed.Length))));
            var name = $"{chunks.Count:D6}.log.gz";
            compressed.Position = 0;
            await blobs.UploadAsync(new(BlobKind.Logs, sha, compressed.Length, prefix + name), compressed, token);
            chunks.Add(new(chunks.Count, name, sha, compressed.Length, bytes, bytes + raw.Length,
                firstLine, lastLine, at, at, bytes, bytes + raw.Length, Uploaded: true, RetainedLines: lastLine - firstLine + 1));
            bytes += raw.Length;
            firstLine = lastLine + 1;
            raw.SetLength(0);
        }
    }
}
