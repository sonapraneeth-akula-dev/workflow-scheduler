using System.Diagnostics;
using System.Text.Json;
using Wf.Chaos.Tests.Infrastructure;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Contracts.Events;

namespace Wf.Chaos.Tests.Scenarios;

public sealed class CoreFaultScenarios(ITestOutputHelper output)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory(Timeout = 240_000), Trait("Category", "ChaosSmoke")]
    [InlineData("leader")]
    [InlineData("agent-partition")]
    [InlineData("database-partition")]
    [InlineData("agent-restart")]
    [InlineData("agent-tree")]
    [InlineData("host")]
    [InlineData("database-failover")]
    [InlineData("disk-full")]
    public async Task Real_fault_recovers_with_one_terminal_result_and_an_unchanged_durable_prefix(string fault)
    {
        await using var fixture = await ChaosFixture.StartAsync(Token);
        if (fault == "database-failover") await fixture.PrepareStandbyAsync(Token);
        await fixture.WaitForHeartbeatsAsync(Token);
        var marker = Path.Combine(fixture.Root, "work-ready");
        var runId = await fixture.StartRunAsync(marker, Token, padSnapshots: fault == "disk-full");
        await fixture.UntilAsync(async () =>
        {
            if (File.Exists(marker)) return true;
            if (await fixture.ScalarAsync<long>("SELECT count(*) FROM runs WHERE id=$1", Token, runId) == 0) return false;
            var status = (await fixture.Store.LoadAsync(runId, Token)).Snapshot.Status;
            Assert.False(status is RunStatus.Succeeded or RunStatus.Failed or RunStatus.Aborted or RunStatus.TimedOut,
                $"Cold SDK preparation ended before fault injection: {status}; " +
                await fixture.ScalarAsync<string>(
                    "SELECT coalesce(string_agg(error,'; '),'') FROM task_attempts WHERE run_id=$1", Token, runId));
            return false;
        }, TimeSpan.FromSeconds(60), Token);
        var host = await fixture.HostAsync(runId, Token);
        using var original = Process.GetProcessById(host.Pid);
        var prefix = await Events(fixture, runId);
        Assert.Equal(RunStatus.Running, (await fixture.Store.LoadAsync(runId, Token)).Snapshot.Status);
        var elapsed = Stopwatch.StartNew();
        var expectedAttempts = 1;
        var expectedStatus = RunStatus.Succeeded;
        DateTimeOffset? reclaimExpiry = null;
        switch (fault)
        {
            case "leader":
                var leader = await fixture.LeaderAsync(Token);
                await leader.KillAsync();
                Assert.NotEqual(leader.Id, (await fixture.LeaderAsync(Token)).Id);
                await Release(marker);
                await Terminal(fixture, runId, TimeSpan.FromSeconds(30) - elapsed.Elapsed);
                Assert.True(elapsed.Elapsed <= TimeSpan.FromSeconds(30), elapsed.Elapsed.ToString());
                break;
            case "agent-partition":
                var peer = fixture.Agents[1 - host.Agent].Id;
                var heartbeat = await fixture.ScalarAsync<DateTimeOffset>(
                    "SELECT last_heartbeat_at FROM agents WHERE id=$1", Token, peer);
                await fixture.SetAgentAvailableAsync(host.Agent, false, Token);
                try
                {
                    await Release(marker);
                    await Task.Delay(TimeSpan.FromSeconds(6), Token);
                    Assert.Equal(RunStatus.Running, (await fixture.Store.LoadAsync(runId, Token)).Snapshot.Status);
                    Assert.True(await fixture.ScalarAsync<DateTimeOffset>(
                        "SELECT last_heartbeat_at FROM agents WHERE id=$1", Token, peer) > heartbeat);
                }
                finally { await fixture.SetAgentAvailableAsync(host.Agent, true, Token); }
                elapsed.Restart();
                await Terminal(fixture, runId, TimeSpan.FromSeconds(30));
                Assert.True(elapsed.Elapsed <= TimeSpan.FromSeconds(30), elapsed.Elapsed.ToString());
                break;
            case "database-partition":
                await fixture.SetDatabaseAvailableAsync(false, Token);
                try
                {
                    await Release(marker);
                    await Task.Delay(TimeSpan.FromSeconds(6), Token);
                    Assert.Equal(prefix, await Events(fixture, runId));
                    Assert.Equal(RunStatus.Running, (await fixture.Store.LoadAsync(runId, Token)).Snapshot.Status);
                }
                finally { await fixture.SetDatabaseAvailableAsync(true, Token); }
                elapsed.Restart();
                await Terminal(fixture, runId, TimeSpan.FromSeconds(30));
                Assert.True(elapsed.Elapsed <= TimeSpan.FromSeconds(30), elapsed.Elapsed.ToString());
                break;
            case "agent-restart":
                await fixture.KillAgentAsync(host.Agent, Token);
                Assert.False(original.HasExited);
                await fixture.RestartAgentAsync(host.Agent, Token);
                Assert.False(original.HasExited);
                await Release(marker);
                await Terminal(fixture, runId, TimeSpan.FromSeconds(30));
                break;
            case "agent-tree":
                var lost = fixture.Agents[host.Agent].Id;
                await fixture.KillAgentAsync(host.Agent, Token, entireProcessTree: true);
                await fixture.UntilAsync(() => Task.FromResult(original.HasExited), TimeSpan.FromSeconds(5), Token);
                Assert.True(original.HasExited);
                var lastHeartbeat = await fixture.ScalarAsync<DateTimeOffset>(
                    "SELECT last_heartbeat_at FROM agents WHERE id=$1", Token, lost);
                reclaimExpiry = lastHeartbeat.AddSeconds(30);
                expectedAttempts = 2;
                await Terminal(fixture, runId, reclaimExpiry.Value.AddSeconds(30) - DateTimeOffset.UtcNow);
                await VerifyReclaim(fixture, runId, lost, lastHeartbeat);
                break;
            case "host":
                await fixture.KillHostAsync(runId, Token);
                Assert.True(original.HasExited);
                expectedAttempts = 2;
                await Terminal(fixture, runId, TimeSpan.FromSeconds(30));
                break;
            case "database-failover":
                await fixture.PromoteStandbyAsync(Token);
                output.WriteLine($"Primary-stop to writable standby: {elapsed.Elapsed.TotalSeconds:F3}s.");
                elapsed.Restart();
                await Release(marker);
                await Terminal(fixture, runId, TimeSpan.FromSeconds(30));
                Assert.True(elapsed.Elapsed <= TimeSpan.FromSeconds(30), elapsed.Elapsed.ToString());
                break;
            case "disk-full":
                await fixture.PrepareSnapshotDiskFaultAsync(runId, Token);
                await fixture.FillSnapshotDiskAsync(Token);
                try
                {
                    await Release(marker);
                    await fixture.UntilAsync(() => Task.FromResult(original.HasExited), TimeSpan.FromSeconds(15), Token);
                    await Task.Delay(TimeSpan.FromSeconds(2), Token);
                    Assert.Equal(RunStatus.Running, (await fixture.Store.LoadAsync(runId, Token)).Snapshot.Status);
                    Assert.Equal(prefix, await Events(fixture, runId));
                }
                finally { await fixture.RecoverSnapshotDiskAsync(Token); }
                elapsed.Restart();
                await Terminal(fixture, runId, TimeSpan.FromSeconds(30));
                Assert.True(elapsed.Elapsed <= TimeSpan.FromSeconds(30), elapsed.Elapsed.ToString());
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(fault));
        }
        Assert.Equal(expectedStatus, (await fixture.Store.LoadAsync(runId, Token)).Snapshot.Status);
        await VerifyHistory(fixture, runId, prefix, expectedAttempts, expectedStatus);
        output.WriteLine($"{fault}: {elapsed.Elapsed.TotalSeconds:F3}s after recovery boundary; " +
            $"attempts={expectedAttempts}; reclaimExpiry={reclaimExpiry:O}; terminal={expectedStatus}; " +
            "contiguous unique lifecycle events, original durable prefix and one terminal run result verified.");
    }

    private static Task Release(string marker) => File.WriteAllTextAsync(marker + ".exit", "1", Token);

    private static async Task Terminal(ChaosFixture fixture, Guid runId, TimeSpan budget)
    {
        Assert.True(budget > TimeSpan.Zero, $"Recovery budget exhausted: {budget}.");
        await fixture.UntilAsync(async () => (await fixture.Store.LoadAsync(runId, Token)).Snapshot.Status
            is RunStatus.Succeeded or RunStatus.Failed or RunStatus.Aborted or RunStatus.TimedOut, budget, Token);
    }

    private static async Task<RunEvent[]> Events(ChaosFixture fixture, Guid runId)
    {
        await using var query = fixture.Source.CreateCommand(
            "SELECT payload FROM run_events WHERE run_day=uuid_v7_run_day($1) AND run_id=$1 ORDER BY seq");
        query.Parameters.AddWithValue(runId);
        var changes = new List<RunEvent>();
        await using var reader = await query.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
            changes.Add(JsonSerializer.Deserialize<RunEvent>(reader.GetFieldValue<byte[]>(0)) ??
                throw new InvalidDataException("Durable chaos event is null."));
        return changes.ToArray();
    }

    private static async Task VerifyHistory(ChaosFixture fixture, Guid runId, RunEvent[] prefix,
        int attempts, RunStatus expected)
    {
        var events = await Events(fixture, runId);
        Assert.Equal(prefix, events.Take(prefix.Length));
        Assert.Equal(Enumerable.Range(1, events.Length).Select(value => (long)value), events.Select(change => change.Sequence));
        Assert.Equal(events.Length, (await fixture.Store.LoadAsync(runId, Token)).Snapshot.LastSequence);
        var lifecycle = events.Where(change => change.WaitingReason is null && change.Progress is null).ToArray();
        Assert.Equal(lifecycle.Length, lifecycle.Select(change => (change.Entity, change.Key, change.Attempt, change.State)).Distinct().Count());
        Assert.Equal((int)expected, Assert.Single(events, change => change.Entity == EventEntity.Run &&
            (RunStatus)change.State is RunStatus.Succeeded or RunStatus.Failed or RunStatus.Aborted or RunStatus.TimedOut).State);
        var actualAttempts = await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM task_attempts WHERE run_day=uuid_v7_run_day($1) AND run_id=$1", Token, runId);
        if (actualAttempts != attempts)
            Assert.Fail($"Expected {attempts} attempts, found {actualAttempts}: " +
                await fixture.ScalarAsync<string>("""
                    SELECT json_agg(row_to_json(t))::text FROM
                        (SELECT attempt,status,failure_category,error,agent_id FROM task_attempts WHERE run_id=$1) t
                    """, Token, runId) + Environment.NewLine +
                string.Join(Environment.NewLine, fixture.Agents.Select(agent => agent.Diagnostics)));
        Assert.Equal(0, await fixture.ScalarAsync<long>("""
            SELECT count(*) FROM task_attempts WHERE run_day=uuid_v7_run_day($1) AND run_id=$1
                AND (status IN (0,1,2,3,4) OR ended_at IS NULL)
            """, Token, runId));
        Assert.Equal(0, await fixture.ScalarAsync<long>("""
            SELECT count(*) FROM dispatch_outbox WHERE run_id=$1
            """, Token, runId));
    }

    private async Task VerifyReclaim(ChaosFixture fixture, Guid runId, Guid lost, DateTimeOffset heartbeat)
    {
        await using var query = fixture.Source.CreateCommand("""
            SELECT l.ended_at,l.failure_category,r.started_at,r.ended_at,r.deadline_at,run.deadline_at,r.agent_id
            FROM task_attempts l JOIN task_attempts r ON r.run_day=l.run_day AND r.run_id=l.run_id
                AND r.task_key=l.task_key AND r.attempt=2
            JOIN runs run ON run.run_day=r.run_day AND run.id=r.run_id
            WHERE l.run_day=uuid_v7_run_day($1) AND l.run_id=$1 AND l.attempt=1 AND l.agent_id=$2
            """);
        query.Parameters.AddWithValue(runId);
        query.Parameters.AddWithValue(lost);
        await using var row = await query.ExecuteReaderAsync(Token);
        Assert.True(await row.ReadAsync(Token));
        var expiry = heartbeat.AddSeconds(30);
        var classified = row.GetFieldValue<DateTimeOffset>(0);
        var started = row.GetFieldValue<DateTimeOffset>(2);
        var completed = row.GetFieldValue<DateTimeOffset>(3);
        Assert.Equal((short)FailureCategory.AgentLost, row.GetInt16(1));
        Assert.True(classified >= expiry, "AgentLost classification shortened the full heartbeat reclaim interval.");
        Assert.InRange(started, classified, expiry.AddSeconds(30));
        Assert.InRange(completed, started, expiry.AddSeconds(30));
        Assert.True(completed <= row.GetFieldValue<DateTimeOffset>(4) && completed <= row.GetFieldValue<DateTimeOffset>(5));
        Assert.NotEqual(lost, row.GetGuid(6));
        Assert.False(await row.ReadAsync(Token));
        output.WriteLine($"heartbeat={heartbeat:O}; expiry={expiry:O}; classified={classified:O}; " +
            $"agentEntry(pre-host)={started:O}; SDK-start upper bound(success)={completed:O}.");
    }
}
