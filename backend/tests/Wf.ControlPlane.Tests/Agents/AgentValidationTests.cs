using Wf.Contracts.Agent.V1;
using Wf.ControlPlane.Engine.Agents;

namespace Wf.ControlPlane.Tests.Agents;

public sealed class AgentValidationTests
{
    public static IEnumerable<object[]> InvalidHellos()
    {
        yield return [new Action<Hello>(hello => hello.Id = "hostname")];
        yield return [new Action<Hello>(hello => hello.Id = Guid.Empty.ToString())];
        yield return [new Action<Hello>(hello => hello.ProtocolVersion = 0)];
        yield return [new Action<Hello>(hello => hello.Name = "")];
        yield return [new Action<Hello>(hello => hello.Pool = "")];
        yield return [new Action<Hello>(hello => hello.Version = "")];
        yield return [new Action<Hello>(hello => hello.Name = new string('a', 201))];
        yield return [new Action<Hello>(hello => hello.Cores = 0)];
        yield return [new Action<Hello>(hello => hello.RamMb = ulong.MaxValue)];
        yield return [new Action<Hello>(hello => hello.MaxTasks = uint.MaxValue)];
        yield return [new Action<Hello>(hello => hello.Os = "unknown")];
        yield return [new Action<Hello>(hello => hello.Arch = "x86")];
        yield return [new Action<Hello>(hello => hello.Labels["key=value"] = "value")];
        yield return [new Action<Hello>(hello => hello.Labels["key"] = "")];
        yield return [new Action<Hello>(hello => hello.Inflight.Add(new AttemptRef()))];
        yield return [new Action<Hello>(hello =>
        {
            var task = new AttemptRef { RunId = Guid.NewGuid().ToString(), NodeKey = "n", TaskKey = "t", Attempt = 1 };
            hello.Inflight.Add(task);
            hello.Inflight.Add(task);
        })];
    }

    [Theory]
    [MemberData(nameof(InvalidHellos))]
    public void Invalid_Hello_is_rejected_before_any_persistence(Action<Hello> mutate)
    {
        var hello = ValidHello();
        mutate(hello);
        Assert.Throws<ArgumentException>(() => AgentRegistry.ValidateHello(hello, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void Healthy_lease_expires_at_exactly_fifteen_seconds_even_before_sweeper_runs()
    {
        var agent = AgentRegistry.ValidateHello(ValidHello(), DateTimeOffset.UnixEpoch);
        Assert.True(agent.IsDispatchable(DateTimeOffset.UnixEpoch.AddSeconds(15).AddTicks(-1)));
        Assert.False(agent.IsDispatchable(DateTimeOffset.UnixEpoch.AddSeconds(15)));
        Assert.False((agent with { Status = AgentStatus.Unhealthy }).IsDispatchable(DateTimeOffset.UnixEpoch));
        Assert.False((agent with { Status = AgentStatus.Draining }).IsDispatchable(DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void Hello_copies_labels_and_counts_inflight_slots()
    {
        var hello = ValidHello();
        hello.Labels["dotnet"] = "10";
        hello.Inflight.Add(new AttemptRef { RunId = Guid.NewGuid().ToString(), NodeKey = "n", TaskKey = "t", Attempt = 1 });
        var agent = AgentRegistry.ValidateHello(hello, DateTimeOffset.UnixEpoch);
        hello.Labels["dotnet"] = "changed";
        Assert.Equal("10", agent.Labels["dotnet"]);
        Assert.Equal(1, agent.SlotsUsed);
        Assert.NotEqual(Guid.Empty, agent.SessionId);
    }

    private static Hello ValidHello() => new()
    {
        Id = Guid.NewGuid().ToString(), Name = "worker", Os = "linux", Arch = "x64", Cores = 4,
        RamMb = 8192, MaxTasks = 2, Pool = "default", ProtocolVersion = 1, Version = "1.0"
    };
}
