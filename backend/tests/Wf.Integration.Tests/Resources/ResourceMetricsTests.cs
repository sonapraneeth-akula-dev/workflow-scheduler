using System.Collections.Immutable;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Wf.Agent;
using Wf.Agent.Journal;
using Wf.Agent.Packages;
using Wf.Contracts;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Events;
using Wf.Contracts.Resources;
using Wf.ControlPlane.Api;
using Wf.ControlPlane.Auth;
using Wf.ControlPlane.Data;
using Wf.ControlPlane.Engine;
using Wf.ControlPlane.Engine.Agents;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Leadership;
using Wf.ControlPlane.Storage;
using Wf.Engine.Core;
using Wf.Engine.Core.Placement;
using Wf.Integration.Tests.Agents;
using Wf.TaskHost.Fixture;
using DispatchCommand = Wf.Contracts.Agent.V1.Dispatch;

namespace Wf.Integration.Tests.Resources;

public sealed class ResourceMetricsTests(ResourceMetricsDatabase database, ITestOutputHelper output) : IClassFixture<ResourceMetricsDatabase>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ResourceMetrics_OpenAPI_exposes_typed_series_queries_and_no_storage_pointer_payload()
    {
        await using var host = await ResourceHost.Start(database.DataSource);
        using var response = await host.Client.GetAsync("/openapi/v1.json", Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        var operation = document.RootElement.GetProperty("paths").GetProperty("/api/v1/runs/{id}/resources").GetProperty("get");
        var parameters = operation.GetProperty("parameters").EnumerateArray().ToArray();
        Assert.All(new[] { "taskKey", "attempt" }, name => Assert.True(Assert.Single(parameters,
            item => item.GetProperty("name").GetString() == name).GetProperty("required").GetBoolean()));
        Assert.All(new[] { "limit", "cursor" }, name => Assert.Single(parameters, item => item.GetProperty("name").GetString() == name));
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        Assert.False(schemas.GetProperty("ResourceSeriesSample").GetProperty("properties").TryGetProperty("previous", out _));
        Assert.True(schemas.GetProperty("ResourcePoint").GetProperty("properties").TryGetProperty("ramBytes", out _));
        Assert.True(schemas.GetProperty("ResourcePoint").GetProperty("properties").TryGetProperty("networkSentBytesPerSecond", out _));
    }

    [Fact]
    public async Task ResourceMetrics_actual_agent_gateway_SDK_host_populates_summary_and_fresh_Viewer_blob_reads()
    {
        await using var host = await ResourceHost.Start(database.DataSource);
        var package = await Package();
        host.App.MapGet("/package.zip", () => Results.Bytes(package, "application/zip"));
        var agentId = Guid.NewGuid();
        using var agent = StartAgent(host, agentId);
        var tracePrefix = OperatingSystem.IsWindows()
            ? $"wf-resource-{agent.Id}-{agent.StartTime.ToUniversalTime().Ticks:x}-" : null;
        var stdout = agent.StandardOutput.ReadToEndAsync(Token);
        var stderr = agent.StandardError.ReadToEndAsync(Token);
        try
        {
            await Wait(() => Task.FromResult(host.Registry.Find(agentId) is not null), agent, stdout, stderr);
            var session = new AgentSession(agentId, host.Registry.Find(agentId)!.SessionId);
            var run = await host.Seed(session, "resources", package);
            await Wait(async () => (await host.Runs.LoadAsync(run.Snapshot.RunId, Token)).Snapshot.Tasks["work/resources"] ==
                ExecutionStatus.Succeeded, agent, stdout, stderr);
            var summaries = await host.Summaries(run.Snapshot.RunId);
            var summary = Assert.Single(summaries);
            output.WriteLine($"Actual {System.Runtime.InteropServices.RuntimeInformation.OSDescription} agent: " +
                JsonSerializer.Serialize(summaries, ResourceJsonContext.Default.ResourceAttemptSummaryArray));
            Assert.True(summary.SampleCount >= 3);
            Assert.True(summary.AverageCpuPercent > 0);
            Assert.True(summary.MaxCpuPercent >= summary.AverageCpuPercent);
            Assert.True(summary.PeakRamBytes >= 32 * 1024 * 1024);
            Assert.True(summary.CpuObservedSeconds > 0);
            Assert.Null(summary.CpuUnavailableReason);
            Assert.Null(summary.MemoryUnavailableReason);
            var samples = new List<ResourceSeriesSample>();
            string? cursor = null;
            do
            {
                using var response = await host.Client.GetAsync(host.Series(run.Snapshot.RunId, "resources", 2, cursor), Token);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
                var text = await response.Content.ReadAsStringAsync(Token);
                Assert.DoesNotContain("\"previous\"", text);
                Assert.DoesNotContain("upload", text, StringComparison.OrdinalIgnoreCase);
                var page = JsonSerializer.Deserialize(text, ResourceJsonContext.Default.ResourceSeriesPage)!;
                samples.AddRange(page.Samples);
                cursor = page.NextCursor;
            } while (cursor is not null);
            Assert.Equal(summary.SampleCount, samples.Count);
            Assert.Equal(Enumerable.Range(1, samples.Count).Reverse().Select(value => (ulong)value), samples.Select(value => value.Sequence));
            var chronological = samples.AsEnumerable().Reverse().ToArray();
            Assert.Null(chronological[0].Observation.Task.CpuPercent);
            Assert.Contains("First", chronological[0].Observation.Task.CpuUnavailableReason);
            Assert.Null(chronological[0].Observation.Task.NetworkSentBytesPerSecond);
            Assert.All(samples, sample =>
            {
                Assert.Equal(TimeSpan.Zero, sample.Observation.Task.AtUtc.Offset);
                Assert.InRange(sample.Observation.Task.IntervalSeconds ?? 0, 0, 5);
                Assert.NotEqual("machine", sample.Observation.Task.CpuScope);
                Assert.NotNull(sample.Observation.Machine);
            });
            var nativeNetwork = samples.Any(sample => sample.Observation.Task.NetworkSentDeltaBytes > 0);
            output.WriteLine($"Actual samples={samples.Count}, maximum monotonic interval seconds=" +
                $"{samples.Max(sample => sample.Observation.Task.IntervalSeconds ?? 0):R}, nativeNetwork={nativeNetwork}.");
            output.WriteLine("Network availability: " + string.Join(" | ", samples.Select(sample =>
                $"{sample.Sequence}:{sample.Observation.Task.NetworkUnavailableReason ?? "available"}/" +
                $"{sample.Observation.Task.NetworkRateUnavailableReason ?? "rate available"}")));
            output.WriteLine("Task scopes: " + string.Join(", ", samples.Select(sample =>
                $"{sample.Observation.Task.CpuScope}/{sample.Observation.Task.MemoryScope}/{sample.Observation.Task.NetworkScope}").Distinct()));
            if (OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("WF_RESOURCE_TEST_CGROUP_ROOT") is not null)
                Assert.All(samples.Where(sample => sample.Observation.Task.CpuPercent is not null), sample =>
                    Assert.Equal(OperatingSystem.IsWindows() ? "job-with-descendants" : "cgroup-with-descendants", sample.Observation.Task.CpuScope));
            if (Environment.GetEnvironmentVariable("WF_RESOURCE_REQUIRE_NETWORK") == "1")
                Assert.True(nativeNetwork, "Configured native fixture must produce actual task network deltas.");
            if (nativeNetwork)
            {
                Assert.True(summary.NetworkSentBytes >= 100 * 1024);
                Assert.True(summary.NetworkReceivedBytes >= 100 * 1024);
                Assert.Null(summary.NetworkUnavailableReason);
                Assert.Equal(samples.Sum(sample => (decimal)(sample.Observation.Task.NetworkSentDeltaBytes ?? 0)), summary.NetworkSentBytes);
                Assert.Equal(OperatingSystem.IsWindows() ? "host-process-etw-transport" : "cgroup-skb-l3-with-descendants",
                    samples.First(sample => sample.Observation.Task.NetworkSentDeltaBytes > 0).Observation.Task.NetworkScope);
            }
            else
            {
                Assert.Null(summary.NetworkSentBytes);
                Assert.NotNull(summary.NetworkUnavailableReason);
                Assert.All(samples, sample => Assert.NotNull(sample.Observation.Task.NetworkUnavailableReason));
            }
            await using (var query = database.DataSource.CreateCommand("""
                SELECT resource_series_uri,row_to_json(task_attempts)::text FROM task_attempts WHERE run_id=$1
                """))
            {
                query.Parameters.AddWithValue(run.Snapshot.RunId);
                await using var row = await query.ExecuteReaderAsync(Token);
                Assert.True(await row.ReadAsync(Token));
                Assert.StartsWith($"resources/{run.Snapshot.RunId:D}/", row.GetString(0));
                Assert.DoesNotContain("cpuScope", row.GetString(1));
                Assert.DoesNotContain("networkRateUnavailableReason", row.GetString(1));
            }
            // Recreate the reader against durable DB + blobs, without the publishing engine's memory.
            var sink = new ResourceSampleSink(database.DataSource, new(database.DataSource, NullLogger<RunStore>.Instance), host.Blobs);
            var pointer = await host.Pointer(run.Snapshot.RunId);
            Assert.Equal(samples[0].Sequence, (await sink.ReadAsync(pointer, Token)).Sequence);
            using (var page = await host.Client.GetAsync(host.Series(run.Snapshot.RunId, "resources", 1), Token))
            {
                var parsed = JsonSerializer.Deserialize(await page.Content.ReadAsStringAsync(Token), ResourceJsonContext.Default.ResourceSeriesPage)!;
                await host.Execute("DELETE FROM role_bindings WHERE principal_id=$1", host.Viewer);
                using var revoked = await host.Client.GetAsync(host.Series(run.Snapshot.RunId, "resources", 1, parsed.NextCursor), Token);
                Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
                using var deniedSummary = await host.Client.GetAsync($"/api/v1/runs/{run.Snapshot.RunId:D}/resource-metrics", Token);
                Assert.Equal(HttpStatusCode.Forbidden, deniedSummary.StatusCode);
            }
        }
        finally
        {
            if (!agent.HasExited) agent.Kill(entireProcessTree: true);
            await agent.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
            if (tracePrefix is not null) await StopOwnedWindowsTraces(tracePrefix);
        }
    }

    private static async Task StopOwnedWindowsTraces(string prefix)
    {
        var output = await Logman("query", "-ets");
        var names = output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(value => value.StartsWith(prefix, StringComparison.Ordinal) &&
                value.Length == prefix.Length + 32 && value[prefix.Length..].All(char.IsAsciiHexDigit)).Distinct();
        foreach (var name in names) _ = await Logman("stop", name, "-ets");

        static async Task<string> Logman(params string[] arguments)
        {
            var start = new ProcessStartInfo("logman.exe")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await process.WaitForExitAsync(timeout.Token); }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }
            }
            Assert.True(process.ExitCode == 0, $"Owned resource trace cleanup failed: {await stderr}");
            return await stdout;
        }
    }

    [Fact]
    public async Task ResourceMetrics_duration_weighted_math_counter_reset_duplicates_and_restart_are_durable()
    {
        await using var host = await ResourceHost.Start(database.DataSource);
        var session = await host.Register();
        var run = await host.Seed(session);
        var points = new[]
        {
            Observation(null, null, 100, null, 100),
            Observation(100, 1, 120, 20, 200),
            Observation(50, 3, 150, 30, 150),
            Observation(null, 1, 5, null, 100, "Counter reset; new baseline."),
            Observation(0, 1, 15, 10, 80)
        };
        points[2] = points[2] with { Task = points[2].Task with { AtUtc = points[0].Task.AtUtc.AddMinutes(-1) } };
        for (var index = 0; index < points.Length; index++)
        {
            var sample = Sample(run.Snapshot.RunId, index + 1, points[index]);
            await host.Sink.AcceptAsync(session, sample, Token);
            await new ResourceSampleSink(database.DataSource, host.Runs, host.Blobs).AcceptAsync(session, sample, Token);
        }
        // Replay an old observation, not only the latest ordinal.
        await host.Sink.AcceptAsync(session, Sample(run.Snapshot.RunId, 1, points[0]), Token);
        var summary = Assert.Single(await host.Summaries(run.Snapshot.RunId));
        Assert.Equal(5, summary.SampleCount);
        Assert.Equal(50, summary.AverageCpuPercent);
        Assert.Equal(100, summary.MaxCpuPercent);
        Assert.Equal(200m, summary.PeakRamBytes);
        Assert.Equal(60m, summary.NetworkSentBytes);
        Assert.Equal(60m, summary.NetworkReceivedBytes);
        Assert.Equal(5, summary.CpuObservedSeconds);
        Assert.Equal(5, summary.NetworkObservedSeconds);
        await Assert.ThrowsAsync<ArgumentException>(() => host.Sink.AcceptAsync(session,
            Sample(run.Snapshot.RunId, 3, Observation(1, 1, 300, 1, 100)), Token));
        await Assert.ThrowsAsync<ArgumentException>(() => host.Sink.AcceptAsync(session,
            Sample(run.Snapshot.RunId, 7, points[0]), Token));
        var epoch = Sample(run.Snapshot.RunId, 6, points[0]);
        epoch.ReservationEpoch++;
        await Assert.ThrowsAsync<ArgumentException>(() => host.Sink.AcceptAsync(session, epoch, Token));
        var foreign = await host.Register();
        await Assert.ThrowsAsync<ArgumentException>(() => host.Sink.AcceptAsync(foreign, Sample(run.Snapshot.RunId, 6, points[0]), Token));
        var current = await host.Registry.RegisterAsync(host.Identity(session.AgentId), Token);
        await Assert.ThrowsAsync<ArgumentException>(() => host.Sink.AcceptAsync(session, Sample(run.Snapshot.RunId, 6, points[0]), Token));
        await host.Runs.AcceptAsync(current, new AgentMessage { TaskStarted = new TaskStarted
        {
            Task = Reference(run.Snapshot.RunId), At = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow)
        } }, Token);
        await host.Runs.AcceptAsync(current, new AgentMessage { TaskCompleted = new TaskCompleted
        {
            Task = Reference(run.Snapshot.RunId), At = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow), Outcome = TaskOutcome.Succeeded
        } }, Token);
        await host.Sink.AcceptAsync(current, Sample(run.Snapshot.RunId, 6, points[0]), Token);
        await host.Sink.AcceptAsync(current, Sample(run.Snapshot.RunId, 7, points[0]), Token);
        await Assert.ThrowsAsync<ArgumentException>(() => host.Sink.AcceptAsync(current,
            Sample(run.Snapshot.RunId, 3, Observation(1, 1, 300, 1, 100)), Token));
        await host.Sink.AcceptAsync(current, Sample(run.Snapshot.RunId, 1, points[0]), Token);
        Assert.Equal(5, Assert.Single(await host.Summaries(run.Snapshot.RunId)).SampleCount);
    }

    [Fact]
    public async Task ResourceMetrics_unavailable_and_zero_are_distinct_contract_rejects_payload_scope_and_nonfinite_values()
    {
        await using var host = await ResourceHost.Start(database.DataSource);
        var session = await host.Register();
        var run = await host.Seed(session);
        var absent = new ResourceObservation(new(DateTimeOffset.UtcNow, null, null, null, null, null, null, null, null, null,
            "process", "process-rss", "unavailable", "Counters unavailable.", "Counters unavailable.",
            "Native network unavailable.", "No network baseline."), null, "Machine unavailable.");
        await host.Sink.AcceptAsync(session, Sample(run.Snapshot.RunId, 1, absent), Token);
        var summary = Assert.Single(await host.Summaries(run.Snapshot.RunId));
        Assert.Null(summary.AverageCpuPercent);
        Assert.Null(summary.MaxCpuPercent);
        Assert.Null(summary.PeakRamBytes);
        Assert.Null(summary.NetworkSentBytes);
        Assert.NotNull(summary.CpuUnavailableReason);
        var valid = JsonSerializer.SerializeToUtf8Bytes(absent, ResourceJsonContext.Default.ResourceObservation);
        var invalid = Encoding.UTF8.GetString(valid).Replace("\"cpuPercent\":null", "\"cpuPercent\":-1", StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => ResourceContract.Parse(Encoding.UTF8.GetBytes(invalid)));
        invalid = Encoding.UTF8.GetString(valid).Replace("\"process\"", "\"machine\"", StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => ResourceContract.Parse(Encoding.UTF8.GetBytes(invalid)));
        invalid = Encoding.UTF8.GetString(valid).Replace("\"task\":{", "\"task\":{\"secret\":\"payload\",", StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => ResourceContract.Parse(Encoding.UTF8.GetBytes(invalid)));
        Assert.Throws<ArgumentException>(() => ResourceContract.Parse(Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(valid).Replace("\"cpuPercent\":null", "\"cpuPercent\":1e999", StringComparison.Ordinal))));
        var baseline = Observation(null, null, 0, null, 0);
        await host.Sink.AcceptAsync(session, Sample(run.Snapshot.RunId, 2, baseline), Token);
        var zero = Observation(0, 1, 0, 0, 0);
        await host.Sink.AcceptAsync(session, Sample(run.Snapshot.RunId, 3, zero), Token);
        summary = Assert.Single(await host.Summaries(run.Snapshot.RunId));
        Assert.Equal(0, summary.AverageCpuPercent);
        Assert.Equal(0m, summary.PeakRamBytes);
        Assert.Equal(0m, summary.NetworkSentBytes);
    }

    [Fact]
    public async Task ResourceMetrics_journal_replays_exact_samples_before_completion_and_ack_removes_them()
    {
        await using var host = await ResourceHost.Start(database.DataSource);
        var agent = Guid.NewGuid();
        AgentMessage expected;
        using (var outbox = new AgentOutbox(new ResultJournal(Path.Combine(host.Root, "journal"), agent.ToString("D"))))
        {
            outbox.Enqueue(new AgentMessage { ResourceSample = Sample(Guid.CreateVersion7(), 1, Observation(null, null, 0, null, 0)) });
            expected = outbox.NextAfter(0)!;
        }
        using var replay = new AgentOutbox(new ResultJournal(Path.Combine(host.Root, "journal"), agent.ToString("D")));
        Assert.Equal(expected, replay.NextAfter(0));
        Assert.Single(replay.UnacknowledgedAttempts());
        replay.Acknowledge(new Ack { Sequence = expected.Sequence });
        Assert.Null(replay.NextAfter(0));
    }

    [Fact]
    public async Task ResourceMetrics_upload_failure_rolls_back_pointer_and_concurrent_retries_commit_once()
    {
        await using var host = await ResourceHost.Start(database.DataSource);
        var session = await host.Register();
        var run = await host.Seed(session);
        var sample = Sample(run.Snapshot.RunId, 1, Observation(null, null, 0, null, 100));
        var failed = new ResourceSampleSink(database.DataSource, host.Runs, new InterruptedBlobStore(host.Blobs));
        await Assert.ThrowsAsync<IOException>(() => failed.AcceptAsync(session, sample, Token));
        Assert.Equal(0, Assert.Single(await host.Summaries(run.Snapshot.RunId)).SampleCount);
        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => host.Sink.AcceptAsync(session, sample, Token)));
        Assert.Equal(1, Assert.Single(await host.Summaries(run.Snapshot.RunId)).SampleCount);
        Assert.Single(Directory.EnumerateFiles(Path.Combine(host.Root, "blobs", "resources"), "*.json", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ResourceMetrics_database_leader_epoch_fences_stale_engine_without_advancing_summary()
    {
        await using var host = await ResourceHost.Start(database.DataSource);
        var session = await host.Register();
        var run = await host.Seed(session);
        using var first = new LeaseManager(database.DataSource, new("resource-first", new Uri("http://127.0.0.1:1")),
            NullLogger<LeaseManager>.Instance);
        using var second = new LeaseManager(database.DataSource, new("resource-second", new Uri("http://127.0.0.1:2")),
            NullLogger<LeaseManager>.Instance);
        try
        {
            Assert.True(await first.TryAcquireOrRenewAsync(Token));
            var oldSink = new ResourceSampleSink(database.DataSource,
                new(database.DataSource, NullLogger<RunStore>.Instance, first), host.Blobs);
            await oldSink.AcceptAsync(session, Sample(run.Snapshot.RunId, 1, Observation(null, null, 0, null, 100)), Token);
            await host.Execute("UPDATE leases SET expires_at=clock_timestamp()-interval '1 second' WHERE name='engine'");
            Assert.True(await second.TryAcquireOrRenewAsync(Token));
            var next = Sample(run.Snapshot.RunId, 2, Observation(10, 1, 10, 10, 100));
            await Assert.ThrowsAsync<NotLeaderException>(() => oldSink.AcceptAsync(session, next, Token));
            Assert.Equal(1, Assert.Single(await host.Summaries(run.Snapshot.RunId)).SampleCount);
            await new ResourceSampleSink(database.DataSource,
                new(database.DataSource, NullLogger<RunStore>.Instance, second), host.Blobs).AcceptAsync(session, next, Token);
            Assert.Equal(2, Assert.Single(await host.Summaries(run.Snapshot.RunId)).SampleCount);
        }
        finally
        {
            await first.ReleaseAsync(CancellationToken.None);
            await second.ReleaseAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResourceMetrics_slow_blob_io_releases_allocation_and_lease_fences(bool download)
    {
        await using var host = await ResourceHost.Start(database.DataSource);
        var session = await host.Register();
        var run = await host.Seed(session);
        using var leader = new LeaseManager(database.DataSource, new("resource-slow", new Uri("http://127.0.0.1:1")),
            NullLogger<LeaseManager>.Instance);
        Assert.True(await leader.TryAcquireOrRenewAsync(Token));
        var lease = leader.RequireLease();
        var runs = new RunStore(database.DataSource, NullLogger<RunStore>.Instance, leader);
        await host.Sink.AcceptAsync(session, Sample(run.Snapshot.RunId, 1, Observation(null, null, 0, null, 100)), Token);
        var paused = new PausedBlobStore(host.Blobs, download);
        var pending = new ResourceSampleSink(database.DataSource, runs, paused).AcceptAsync(session,
            Sample(run.Snapshot.RunId, 2, Observation(10, 1, 10, 10, 100)), Token);
        try
        {
            await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            await Task.Delay(TimeSpan.FromMilliseconds(1250), Token);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            Assert.True(await leader.TryAcquireOrRenewAsync(timeout.Token));
            Assert.Equal(lease, leader.RequireLease());
            await using var connection = await database.DataSource.OpenConnectionAsync(timeout.Token);
            await using var transaction = await connection.BeginTransactionAsync(timeout.Token);
            await leader.FenceAsync(connection, transaction, lease, timeout.Token);
            await using var gate = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock(746312081::bigint)", connection);
            Assert.Equal(true, await gate.ExecuteScalarAsync(timeout.Token));
            await transaction.CommitAsync(timeout.Token);
        }
        finally
        {
            paused.Release.TrySetResult();
            try { await pending; }
            finally { await leader.ReleaseAsync(CancellationToken.None); }
        }
        Assert.Equal(2, Assert.Single(await host.Summaries(run.Snapshot.RunId)).SampleCount);
    }

    [Theory]
    [InlineData("session")]
    [InlineData("reservation")]
    [InlineData("leader")]
    [InlineData("terminal")]
    [InlineData("duplicate")]
    [InlineData("conflict")]
    public async Task ResourceMetrics_blob_publication_rechecks_authority_and_concurrent_pointer(string change)
    {
        await using var host = await ResourceHost.Start(database.DataSource);
        var session = await host.Register();
        var run = await host.Seed(session);
        using var first = new LeaseManager(database.DataSource, new("resource-publish-first", new Uri("http://127.0.0.1:1")),
            NullLogger<LeaseManager>.Instance);
        using var second = new LeaseManager(database.DataSource, new("resource-publish-second", new Uri("http://127.0.0.1:2")),
            NullLogger<LeaseManager>.Instance);
        Assert.True(await first.TryAcquireOrRenewAsync(Token));
        await host.Sink.AcceptAsync(session, Sample(run.Snapshot.RunId, 1, Observation(null, null, 0, null, 100)), Token);
        var paused = new PausedBlobStore(host.Blobs, download: false);
        var sample = Sample(run.Snapshot.RunId, 2, Observation(10, 1, 10, 10, 100));
        var pending = new ResourceSampleSink(database.DataSource,
            new(database.DataSource, NullLogger<RunStore>.Instance, first), paused).AcceptAsync(session, sample, Token);
        try
        {
            await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            switch (change)
            {
                case "session":
                    await host.Registry.RegisterAsync(host.Identity(session.AgentId), Token);
                    break;
                case "reservation":
                    await host.Execute("UPDATE agents SET reservation_epoch=reservation_epoch+1 WHERE id=$1", session.AgentId);
                    break;
                case "leader":
                    await host.Execute("UPDATE leases SET expires_at=clock_timestamp()-interval '1 second' WHERE name='engine'");
                    Assert.True(await second.TryAcquireOrRenewAsync(Token));
                    break;
                case "terminal":
                    await host.Execute("UPDATE task_attempts SET status=5 WHERE run_id=$1", run.Snapshot.RunId);
                    break;
                case "duplicate":
                    await host.Sink.AcceptAsync(session, sample, Token);
                    break;
                case "conflict":
                    await host.Sink.AcceptAsync(session,
                        Sample(run.Snapshot.RunId, 2, Observation(50, 1, 10, 10, 200)), Token);
                    break;
            }
        }
        finally { paused.Release.TrySetResult(); }
        try
        {
            if (change == "leader")
                await Assert.ThrowsAsync<NotLeaderException>(() => pending);
            else if (change is "session" or "conflict")
                await Assert.ThrowsAsync<ArgumentException>(() => pending);
            else
                await pending;
            var summary = Assert.Single(await host.Summaries(run.Snapshot.RunId));
            Assert.Equal(change is "duplicate" or "conflict" ? 2 : 1, summary.SampleCount);
            if (change == "duplicate") Assert.Equal(10, summary.AverageCpuPercent);
            if (change == "conflict") Assert.Equal(50, summary.AverageCpuPercent);
        }
        finally
        {
            await first.ReleaseAsync(CancellationToken.None);
            await second.ReleaseAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ResourceMetrics_opaque_cursors_fresh_roles_corruption_and_run_retention_fail_closed()
    {
        await using var host = await ResourceHost.Start(database.DataSource);
        var session = await host.Register();
        var run = await host.Seed(session);
        await host.Sink.AcceptAsync(session, Sample(run.Snapshot.RunId, 1, Observation(null, null, 0, null, 100)), Token);
        await host.Sink.AcceptAsync(session, Sample(run.Snapshot.RunId, 2, Observation(1, 1, 1, 1, 100)), Token);
        using var response = await host.Client.GetAsync(host.Series(run.Snapshot.RunId, "resources", 1), Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(Token), ResourceJsonContext.Default.ResourceSeriesPage)!;
        using (var tampered = await host.Client.GetAsync(host.Series(run.Snapshot.RunId, "resources", 1, page.NextCursor + "x"), Token))
            Assert.Equal(HttpStatusCode.BadRequest, tampered.StatusCode);
        var other = await host.Seed(session);
        using (var cross = await host.Client.GetAsync(host.Series(other.Snapshot.RunId, "resources", 1, page.NextCursor), Token))
            Assert.Equal(HttpStatusCode.BadRequest, cross.StatusCode);
        using (var badQuery = await host.Client.GetAsync(host.Series(run.Snapshot.RunId, "resources", 121), Token))
            Assert.Equal(HttpStatusCode.BadRequest, badQuery.StatusCode);
        using (var unknown = await host.Client.GetAsync($"/api/v1/runs/{Guid.CreateVersion7():D}/resource-metrics", Token))
            Assert.Equal(HttpStatusCode.Forbidden, unknown.StatusCode);
        var pointer = await host.Pointer(run.Snapshot.RunId);
        var file = Path.Combine(host.Root, "blobs", pointer.Key.Replace('/', Path.DirectorySeparatorChar));
        var original = await File.ReadAllBytesAsync(file, Token);
        await File.WriteAllBytesAsync(file, new byte[original.Length], Token);
        using (var corrupted = await host.Client.GetAsync(host.Series(run.Snapshot.RunId, "resources", 1), Token))
            Assert.Equal(HttpStatusCode.ServiceUnavailable, corrupted.StatusCode);
        await File.WriteAllBytesAsync(file, original, Token);
        var sweep = new ResourceRetentionService(database.DataSource, host.Blobs, NullLogger<ResourceRetentionService>.Instance);
        await sweep.SweepAsync(Token);
        Assert.True(File.Exists(file));
        await host.Execute("""
            DELETE FROM dispatch_outbox WHERE run_id=$1;
            DELETE FROM task_attempts WHERE run_id=$1;
            DELETE FROM node_runs WHERE run_id=$1;
            DELETE FROM run_snapshots WHERE run_id=$1;
            DELETE FROM run_events WHERE run_id=$1;
            DELETE FROM runs WHERE id=$1;
            """, run.Snapshot.RunId);
        await sweep.SweepAsync(Token);
        Assert.False(Directory.Exists(Path.Combine(host.Root, "blobs", "resources", run.Snapshot.RunId.ToString("D"))));
        using var deleted = await host.Client.GetAsync(host.Series(run.Snapshot.RunId, "resources", 1, page.NextCursor), Token);
        Assert.Equal(HttpStatusCode.Forbidden, deleted.StatusCode);
    }

    private static ResourceObservation Observation(double? cpu, double? seconds, ulong bytes, ulong? delta, ulong ram, string? reason = null) =>
        new(new(DateTimeOffset.UtcNow, seconds, cpu, ram, bytes, bytes, delta, delta,
            delta is null ? null : delta / seconds, delta is null ? null : delta / seconds,
            "process", "process-rss", "host-process-etw-transport",
            cpu is null ? reason ?? "First CPU sample." : null, null, null, delta is null ? reason ?? "First network sample." : null),
            null, "No machine observation in mathematical fixture.");

    private static AttemptRef Reference(Guid run) => new() { RunId = run.ToString("D"), NodeKey = "work", TaskKey = "resources", Attempt = 1 };
    private static ResourceSample Sample(Guid run, int ordinal, ResourceObservation observation) => new()
    {
        Task = Reference(run), ReservationEpoch = 1, SampleSequence = (ulong)ordinal,
        SampleJson = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes(observation, ResourceJsonContext.Default.ResourceObservation))
    };

    private static async Task<byte[]> Package()
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var path in Directory.EnumerateFiles(AppContext.BaseDirectory).Where(path => Path.GetExtension(path) is ".dll" or ".json"))
            {
                await using var source = File.OpenRead(path);
                await using var destination = zip.CreateEntry(Path.GetFileName(path), CompressionLevel.Fastest).Open();
                await source.CopyToAsync(destination, Token);
            }
            await using var manifest = new StreamWriter(zip.CreateEntry(PackageCache.ManifestName).Open(), Encoding.UTF8);
            await manifest.WriteAsync($$"""{"version":"1.0.0","entryAssembly":"{{Path.GetFileName(typeof(FixtureMarker).Assembly.Location)}}"}""");
        }
        return buffer.ToArray();
    }

    private static Process StartAgent(ResourceHost host, Guid id)
    {
        var native = Environment.GetEnvironmentVariable("WF_RESOURCE_AGENT_EXECUTABLE");
        if (native is not null) Assert.True(File.Exists(native), $"Configured agent binary is missing: {native}");
        var start = new ProcessStartInfo(native ?? "dotnet") { UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, WorkingDirectory = host.Root };
        if (native is null)
            foreach (var argument in new[] { "exec", "--runtimeconfig", Path.ChangeExtension(typeof(ResourceMetricsTests).Assembly.Location, ".runtimeconfig.json"),
                         "--depsfile", Path.ChangeExtension(typeof(ResourceMetricsTests).Assembly.Location, ".deps.json"), typeof(AgentConnection).Assembly.Location })
                start.ArgumentList.Add(argument);
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("Agent__", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        start.Environment["Agent__Id"] = id.ToString("D");
        start.Environment["Agent__Name"] = "resources-" + id.ToString("N");
        start.Environment["Agent__Pool"] = host.Pool;
        start.Environment["Agent__MaxTasks"] = "1";
        start.Environment["Agent__Engines__0"] = host.Gateway.ToString();
        start.Environment["Agent__ResultJournalDirectory"] = Path.Combine(host.Root, "agent-journal");
        start.Environment["Agent__PackageCache"] = Path.Combine(host.Root, "packages");
        start.Environment["Agent__Sampling__Enabled"] = "true";
        var cgroup = Environment.GetEnvironmentVariable("WF_RESOURCE_TEST_CGROUP_ROOT");
        if (cgroup is not null) start.Environment["Agent__Sampling__LinuxCgroupRoot"] = cgroup;
        var ipc = Environment.GetEnvironmentVariable("WF_RESOURCE_TEST_IPC_ROOT");
        if (ipc is not null)
        {
            start.Environment["TEMP"] = ipc;
            start.Environment["TMP"] = ipc;
            start.Environment["TMPDIR"] = ipc;
        }
        return Process.Start(start)!;
    }

    private static async Task Wait(Func<Task<bool>> predicate, Process agent, Task<string> stdout, Task<string> stderr)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            while (!await predicate())
            {
                if (agent.HasExited) Assert.Fail($"Actual agent exited: {await stdout} {await stderr}");
                await Task.Delay(25, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            if (!agent.HasExited) agent.Kill(entireProcessTree: true);
            await agent.WaitForExitAsync(CancellationToken.None);
            throw new TimeoutException($"Resource observation exceeded 45 seconds. {await stdout} {await stderr}");
        }
    }

    private sealed class ResourceHost : IAsyncDisposable
    {
        public required WebApplication App { get; init; }
        public required HttpClient Client { get; init; }
        public required Uri Gateway { get; init; }
        public required string Root { get; init; }
        public required FileSystemBlobStore Blobs { get; init; }
        public required RunStore Runs { get; init; }
        public required AgentRegistry Registry { get; init; }
        public required ResourceSampleSink Sink { get; init; }
        private NpgsqlDataSource Source { get; init; } = null!;
        public Guid Viewer { get; } = Guid.NewGuid();
        public string Pool { get; } = "resources-" + Guid.NewGuid().ToString("N");
        private readonly string team = "resources-" + Guid.NewGuid().ToString("N");
        private bool seeded;

        public static async Task<ResourceHost> Start(NpgsqlDataSource source)
        {
            var root = Path.GetFullPath(Path.Combine(Environment.GetEnvironmentVariable("WF_RESOURCE_TEST_ARTIFACTS") ??
                Path.Combine(Path.GetTempPath(), "wf-resources"), "tests", Guid.NewGuid().ToString("N")[..12]));
            var key = RandomNumberGenerator.GetBytes(32);
            var blobs = new FileSystemBlobStore(Path.Combine(root, "blobs"), key);
            var runs = new RunStore(source, NullLogger<RunStore>.Instance);
            var registry = new AgentRegistry(source, TimeProvider.System);
            var sink = new ResourceSampleSink(source, runs, blobs);
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http1);
                options.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http2);
            });
            builder.Services.AddGrpc();
            builder.Services.AddAuthentication("fixture").AddScheme<AuthenticationSchemeOptions, ViewerAuthentication>("fixture", _ => { });
            builder.Services.AddTeamAuthorization();
            builder.Services.AddSingleton(source);
            builder.Services.AddSingleton(registry);
            builder.Services.AddSingleton(runs);
            builder.Services.AddSingleton(sink);
            builder.Services.AddSingleton(new ResourceReadCursors(key, TimeProvider.System));
            var documentName = "v1";
            builder.Services.AddOpenApi(documentName);
            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapGrpcService<AgentGatewayService>();
            app.MapResourceMetricsEndpoints();
            app.MapOpenApi();
            await app.StartAsync(Token);
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.ToArray();
            var host = new ResourceHost { App = app, Root = root, Blobs = blobs, Runs = runs, Registry = registry, Sink = sink, Source = source,
                Client = new() { BaseAddress = new(addresses[0]) }, Gateway = new(addresses[1]) };
            host.Client.DefaultRequestHeaders.Add("X-Viewer", host.Viewer.ToString("D"));
            return host;
        }

        public Hello Identity(Guid id) => new()
        {
            Id = id.ToString("D"), Name = "resources-" + id.ToString("N"), Os = OperatingSystem.IsWindows() ? "windows" : "linux",
            Arch = "x64", Cores = 2, RamMb = 8192, MaxTasks = 10, Pool = Pool, Version = "1", ProtocolVersion = 1
        };
        public Task<AgentSession> Register() => Registry.RegisterAsync(Identity(Guid.NewGuid()), Token);

        public async Task<RunState> Seed(AgentSession session, string task = "resources", byte[]? package = null)
        {
            if (!seeded)
            {
                await Execute("INSERT INTO teams(id,name) VALUES($1,$1)", team);
                await Execute("""
                    INSERT INTO workflow_types(id,team_id,name,normalized_name,description,metadata_schema)
                    VALUES($1||'/metrics',$1,'metrics','metrics','resources','{}');
                    INSERT INTO workflows(id,type_id) VALUES($1||'/metrics/workflow',$1||'/metrics');
                    """, team);
                await Execute("""
                    INSERT INTO role_bindings(id,scope_kind,scope_id,role,principal_id,principal_kind) VALUES($1,1,$2,0,$3,1)
                    """, Guid.CreateVersion7(), team, Viewer);
                seeded = true;
            }
            var run = new RunState(Guid.CreateVersion7(), [new("work", [], [new(task)])],
                new("metrics", ImmutableSortedDictionary<string, string>.Empty), Assert.Fail);
            await Runs.CreateAsync(run, team + "/metrics/workflow", Guid.CreateVersion7(), DateTimeOffset.UtcNow.AddMinutes(5), Token);
            await Runs.AppendAsync(run, EventEntity.Run, null, (int)RunStatus.Running, DateTimeOffset.UtcNow, Token);
            await Runs.AppendAsync(run, EventEntity.Task, "work/" + task, (int)ExecutionStatus.Ready, DateTimeOffset.UtcNow, Token);
            var sha = package is null ? new string('a', 64) : Convert.ToHexStringLower(SHA256.HashData(package));
            using var dispatcher = new Dispatcher(Source, Runs, Registry, new PlacementEngine(), TimeProvider.System);
            var decision = await dispatcher.DispatchAsync(run,
                new(run.Snapshot.RunId, "work", task, 1, sha, new(new(pool: Pool))), new DispatchCommand
                {
                    Task = new() { RunId = run.Snapshot.RunId.ToString("D"), NodeKey = "work", TaskKey = task, Attempt = 1 },
                    Version = "1.0.0", PackageUri = new Uri(Client.BaseAddress!, "/package.zip").ToString(), Sha256 = sha,
                    DeadlineAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(2))
                }, Token);
            Assert.Equal(session.AgentId, decision.Agent?.Id);
            return run;
        }

        public string Series(Guid run, string task, int limit, string? cursor = null) =>
            $"/api/v1/runs/{run:D}/resources?taskKey=work%2F{task}&attempt=1&limit={limit}" +
            (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor));
        public async Task<ResourceAttemptSummary[]> Summaries(Guid run)
        {
            using var response = await Client.GetAsync($"/api/v1/runs/{run:D}/resource-metrics", Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(Token), ResourceJsonContext.Default.ResourceAttemptSummaryArray)!;
        }
        public async Task<ResourcePointer> Pointer(Guid run)
        {
            await using var query = Source.CreateCommand("SELECT resource_series_uri,resource_series_sha256,resource_series_bytes FROM task_attempts WHERE run_id=$1");
            query.Parameters.AddWithValue(run);
            await using var row = await query.ExecuteReaderAsync(Token);
            Assert.True(await row.ReadAsync(Token));
            return new(row.GetString(0), row.GetString(1), row.GetInt64(2));
        }
        public async Task Execute(string sql, params object[] values)
        {
            foreach (var statement in sql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                await using var command = Source.CreateCommand(statement);
                foreach (var value in values) command.Parameters.AddWithValue(value);
                await command.ExecuteNonQueryAsync(Token);
            }
        }
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.StopAsync(CancellationToken.None);
            await App.DisposeAsync();
            Registry.Dispose();
            Blobs.Dispose();
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class ViewerAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(Guid.TryParse(Request.Headers["X-Viewer"], out var viewer)
                ? AuthenticateResult.Success(new(new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim("oid", viewer.ToString("D")), new Claim("idtyp", "user")], Scheme.Name)), Scheme.Name))
                : AuthenticateResult.NoResult());
    }

    private sealed class InterruptedBlobStore(IBlobStore inner) : IBlobStore
    {
        public async Task UploadAsync(BlobContent content, Stream source, CancellationToken cancellationToken = default)
        {
            await inner.UploadAsync(content, source, cancellationToken);
            throw new IOException("Injected interruption after immutable publication, before the DB pointer commit.");
        }
        public Task DownloadAsync(BlobContent content, Stream destination, CancellationToken cancellationToken = default) =>
            inner.DownloadAsync(content, destination, cancellationToken);
    }

    private sealed class PausedBlobStore(IBlobStore inner, bool download) : IBlobStore
    {
        private int entered;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private async Task Pause(CancellationToken token)
        {
            if (Interlocked.Exchange(ref entered, 1) != 0) return;
            Entered.TrySetResult();
            await Release.Task.WaitAsync(token);
        }

        public async Task UploadAsync(BlobContent content, Stream source, CancellationToken cancellationToken = default)
        {
            if (!download) await Pause(cancellationToken);
            await inner.UploadAsync(content, source, cancellationToken);
        }

        public async Task DownloadAsync(BlobContent content, Stream destination, CancellationToken cancellationToken = default)
        {
            if (download) await Pause(cancellationToken);
            await inner.DownloadAsync(content, destination, cancellationToken);
        }
    }
}

public sealed class ResourceMetricsDatabase : AgentDatabase
{
    protected override IReadOnlyList<string> Migrations => Migrator.LoadScripts().Select(script => script.Name).ToArray();
}
