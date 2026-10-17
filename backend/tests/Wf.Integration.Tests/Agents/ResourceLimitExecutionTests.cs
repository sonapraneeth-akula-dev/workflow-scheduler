using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Npgsql;
using Wf.Agent;
using Wf.Agent.Journal;
using Wf.Contracts;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Definition;
using Wf.ControlPlane.Engine.Agents;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.Engine.Core.Placement;
using Wf.Integration.Tests.Reports;
using FixtureAgent = Wf.Integration.Tests.Checkpoints.CheckpointProducerTests.Agent;
using ReportHost = Wf.Integration.Tests.Reports.ReportPublishTests.ReportHost;

namespace Wf.Integration.Tests.Agents;

[Collection("Agent process containment")]
public sealed class ResourceLimitExecutionTests(ReportPublishDatabase database) : IClassFixture<ReportPublishDatabase>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public async Task Real_RAM_breach_is_durable_only_after_physical_cleanup_and_never_retries_even_after_reconnect(int mode, bool reconnect)
    {
        var delegated = Environment.GetEnvironmentVariable("WF_RESOURCE_TEST_CGROUP_ROOT");
        Assert.SkipWhen(OperatingSystem.IsLinux() && delegated is null, "Requires an isolated delegated cgroup-v2 root.");
        await using var host = await ReportHost.Start(database.DataSource);
        var archive = await ReportPublishTests.Package();
        host.App.MapGet("/limit-package.zip", () => Results.Bytes(archive, "application/zip"));
        var hash = Convert.ToHexStringLower(SHA256.HashData(archive));
        var id = Guid.NewGuid();
        await using var agent = new FixtureAgent(host, id, linuxCgroupRoot: delegated);
        await Until(() => Task.FromResult(host.Registry.Find(id) is not null), agent.Work);
        var session = new AgentSession(id, host.Registry.Find(id)!.SessionId);
        var attempt = await host.Seed(dispatch: false, task: "resourceMemory");
        var marker = Path.Combine(host.Root, "ram-workload");
        var dispatch = new Dispatch
        {
            Task = new() { RunId = attempt.RunId.ToString("D"), NodeKey = "$post", TaskKey = "resourceMemory", Attempt = 1 },
            Version = "1.0.0", Sha256 = hash, PackageUri = new Uri(host.Address, "/limit-package.zip").AbsoluteUri,
            Limits = new() { CpuCores = 0.5, RamMb = 192 },
            DeadlineAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(60)),
            VariablesJson = ByteString.CopyFromUtf8(JsonSerializer.Serialize(new { checkpointMarker = marker, amount = mode }))
        };
        var policy = new ExecutionPolicy(TimeSpan.FromMinutes(1),
            new RetryPolicy(3, on: [FailureCategory.ResourceLimit, FailureCategory.PlatformError]), limits: new(0.5, 192));
        using var dispatcher = new Dispatcher(database.DataSource, host.Runs, host.Registry, new PlacementEngine(), TimeProvider.System);
        Assert.Equal(id, (await dispatcher.DispatchAsync(await host.Runs.LoadAsync(attempt.RunId, Token),
            new(attempt.RunId, "$post", "resourceMemory", 1, hash, new(new(pool: host.Pool)), RequiredRamMb: 192),
            dispatch, policy, Token)).Agent!.Id);
        await Until(() => Task.FromResult(File.Exists(marker)), agent.Work);
        await using var ready = new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        using var started = await JsonDocument.ParseAsync(ready, cancellationToken: Token);
        var worker = started.RootElement.GetProperty("pid").GetInt32();
        var supervisor = started.RootElement.GetProperty("supervisor").GetInt32();
        await using (var barrier = await database.DataSource.OpenConnectionAsync(Token))
        await using (var transaction = await barrier.BeginTransactionAsync(Token))
        {
            await using (var hold = new NpgsqlCommand("LOCK TABLE task_attempts IN ACCESS EXCLUSIVE MODE", barrier))
                await hold.ExecuteNonQueryAsync(Token);
            try
            {
                await File.WriteAllTextAsync(marker + ".go", "1", Token);
                await Until(() => Task.FromResult(Pending(agent.Outbox) is not null), agent.Work);
                var completion = Pending(agent.Outbox)!;
                Assert.Equal(TaskOutcome.Failed, completion.Outcome);
                Assert.Equal("ResourceLimit", completion.Error.Category);
                Assert.True(completion.Error.NonRetryable);
                Assert.True(completion.OutputsJson.IsEmpty);
                AssertStopped(worker);
                AssertStopped(supervisor);
                if (mode == 1)
                    AssertStopped(int.Parse(await File.ReadAllTextAsync(marker + ".child", Token),
                        System.Globalization.CultureInfo.InvariantCulture));
                await Until(() => Task.FromResult(agent.Commands.Inflight.Count == 0), agent.Work);
                if (reconnect)
                {
                    await agent.Stop();
                    using var restoredJournal = new AgentOutbox(new ResultJournal(agent.Journal, id.ToString("D")));
                    Assert.Equal(completion, Pending(restoredJournal));
                }
            }
            finally { await transaction.CommitAsync(Token); }
        }
        await using var restored = reconnect ? new FixtureAgent(host, id, linuxCgroupRoot: delegated) : null;
        await Until(async () => (await host.Runs.LoadAsync(attempt.RunId, Token)).Snapshot.Tasks["$post/resourceMemory"] ==
            ExecutionStatus.Failed, restored?.Work ?? agent.Work);
        if (restored is not null)
        {
            Assert.NotEqual(session.SessionId, host.Registry.Find(id)!.SessionId);
            Assert.Equal(0, restored.Hosts.ProcessStartCount);
        }
        await using var query = database.DataSource.CreateCommand("""
            SELECT count(*),min(failure_category),min(author_retries),min(platform_retries),bool_and(retry_at IS NULL)
            FROM task_attempts WHERE run_id=$1 AND task_key='$post/resourceMemory'
            """);
        query.Parameters.AddWithValue(attempt.RunId);
        await using var reader = await query.ExecuteReaderAsync(Token);
        Assert.True(await reader.ReadAsync(Token));
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal((short)FailureCategory.ResourceLimit, reader.GetInt16(1));
        Assert.Equal((short)0, reader.GetInt16(2));
        Assert.Equal((short)0, reader.GetInt16(3));
        Assert.True(reader.GetBoolean(4));
        if (delegated is not null) Assert.Empty(Directory.GetDirectories(delegated, "wf-limits-*"));
    }

    private static TaskCompleted? Pending(AgentOutbox outbox)
    {
        ulong sequence = 0;
        while (outbox.NextAfter(sequence) is { } message)
        {
            sequence = message.Sequence;
            if (message.TaskCompleted is not null) return message.TaskCompleted;
        }
        return null;
    }

    private static async Task Until(Func<Task<bool>> condition, Task work)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(40));
        while (!await condition())
        {
            if (work.IsCompleted) await work;
            await Task.Delay(10, deadline.Token);
        }
    }

    private static void AssertStopped(int pid)
    {
        try { using var process = Process.GetProcessById(pid); Assert.True(process.HasExited, $"Owned process {pid} is still alive."); }
        catch (ArgumentException) { }
    }
}
