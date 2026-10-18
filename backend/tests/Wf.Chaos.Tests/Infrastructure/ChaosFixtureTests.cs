using System.Diagnostics;
using Npgsql;
using Wf.Contracts;

namespace Wf.Chaos.Tests.Infrastructure;

public sealed class ChaosFixtureTests(ITestOutputHelper output)
{
    [Fact(Timeout = 240_000), Trait("Category", "ChaosSmoke")]
    public async Task Two_real_engines_and_agents_support_database_and_agent_partitions_process_kills_and_bounded_disk_full()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await ChaosFixture.StartAsync(token);
        Assert.Equal(2, fixture.Engines.Count);
        Assert.Equal(2, fixture.Agents.Count);
        Assert.All(fixture.Engines, engine => Assert.False(engine.HasExited));
        Assert.All(fixture.Agents, agent => Assert.False(agent.HasExited));
        await fixture.ProbeDatabaseProxyAsync(token);
        await fixture.SetDatabaseAvailableAsync(false, token);
        try { await Assert.ThrowsAsync<NpgsqlException>(() => fixture.ProbeDatabaseProxyAsync(token)); }
        finally { await fixture.SetDatabaseAvailableAsync(true, token); }
        await fixture.ProbeDatabaseProxyAsync(token);

        var heartbeat = await fixture.ScalarAsync<DateTimeOffset>("SELECT last_heartbeat_at FROM agents WHERE id=$1",
            token, fixture.Agents[0].Id);
        await fixture.SetAgentAvailableAsync(0, false, token);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(6), token);
            Assert.Equal(heartbeat, await fixture.ScalarAsync<DateTimeOffset>("SELECT last_heartbeat_at FROM agents WHERE id=$1",
                token, fixture.Agents[0].Id));
            Assert.True(await fixture.ScalarAsync<DateTimeOffset>("SELECT last_heartbeat_at FROM agents WHERE id=$1",
                token, fixture.Agents[1].Id) > heartbeat);
        }
        finally { await fixture.SetAgentAvailableAsync(0, true, token); }
        await fixture.UntilAsync(async () => await fixture.ScalarAsync<DateTimeOffset>(
            "SELECT last_heartbeat_at FROM agents WHERE id=$1", token, fixture.Agents[0].Id) > heartbeat,
            TimeSpan.FromSeconds(30), token);

        var leader = await fixture.LeaderAsync(token);
        var takeover = Stopwatch.StartNew();
        await leader.KillAsync();
        Assert.NotEqual(leader.Id, (await fixture.LeaderAsync(token)).Id);
        Assert.True(takeover.Elapsed <= TimeSpan.FromSeconds(30), takeover.Elapsed.ToString());
        var session = await fixture.ScalarAsync<Guid>("SELECT session_id FROM agents WHERE id=$1", token, fixture.Agents[0].Id);
        await fixture.KillAgentAsync(0, token);
        Assert.True(fixture.Agents[0].HasExited);
        await fixture.RestartAgentAsync(0, token);
        Assert.NotEqual(session, await fixture.ScalarAsync<Guid>("SELECT session_id FROM agents WHERE id=$1",
            token, fixture.Agents[0].Id));

        await fixture.WaitForHeartbeatsAsync(token);
        var marker = Path.Combine(fixture.Root, "host-ready");
        var runId = await fixture.StartRunAsync(marker, token);
        await fixture.UntilAsync(() => Task.FromResult(File.Exists(marker)), TimeSpan.FromSeconds(60), token);
        var host = await fixture.HostAsync(runId, token);
        using (var owned = Process.GetProcessById(host.Pid))
        {
            await fixture.KillHostAsync(runId, token);
            Assert.True(owned.HasExited);
        }
        await fixture.UntilAsync(async () => (await fixture.Store.LoadAsync(runId, token)).Snapshot.Status
            is RunStatus.Succeeded or RunStatus.Failed, TimeSpan.FromSeconds(30), token);
        Assert.Contains("No space left on device", await fixture.FillDiskAsync(token), StringComparison.Ordinal);
        await fixture.RecoverDiskAsync(token);
        output.WriteLine("Actual fault tools verified: two engine/agent processes, Toxiproxy DB and isolated agent partitions, " +
            $"leader kill/takeover <=30s, agent kill/restart, SDK host kill and 8 MiB container-only ENOSPC/recovery.");
    }
}
