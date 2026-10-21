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
        yield return [new Action<Hello>(hello => hello.ProcessInstanceId = "invalid")];
        yield return [new Action<Hello>(hello => hello.ProcessInstanceId = Guid.Empty.ToString("D"))];
        yield return [new Action<Hello>(hello => hello.ProcessInstanceId = Guid.NewGuid().ToString("N"))];
        yield return [new Action<Hello>(hello => hello.ProcessInstanceId = "A2111FD3-F78E-455B-BBE0-75AEAE01FDD1")];
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
    [InlineData("")]
    [InlineData("a2111fd3-f78e-455b-bbe0-75aeae01fdd1")]
    public void Process_identity_is_optional_for_legacy_agents_and_canonical_when_present(string process)
    {
        var hello = ValidHello();
        hello.ProcessInstanceId = process;
        Assert.NotEqual(Guid.Empty, AgentRegistry.ValidateHello(hello, DateTimeOffset.UnixEpoch).SessionId);
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

    [Fact]
    public void Logical_result_backlog_may_exceed_physical_slots_without_truncation()
    {
        var hello = ValidHello();
        for (var index = 0; index < AgentRegistry.MaximumHelloAttempts; index++)
            hello.Inflight.Add(new AttemptRef { RunId = Guid.NewGuid().ToString(), NodeKey = "n", TaskKey = "t", Attempt = 1 });
        var agent = AgentRegistry.ValidateHello(hello, DateTimeOffset.UnixEpoch);
        Assert.Equal((int)hello.MaxTasks, agent.SlotsUsed);
        Assert.Equal(AgentRegistry.MaximumHelloAttempts, hello.Inflight.Count);
        hello.Inflight.Add(new AttemptRef { RunId = Guid.NewGuid().ToString(), NodeKey = "n", TaskKey = "t", Attempt = 1 });
        var error = Assert.Throws<ArgumentException>(() => AgentRegistry.ValidateHello(hello, DateTimeOffset.UnixEpoch));
        Assert.Contains("inventory", error.Message);
    }

    [Fact]
    public void Logical_inventory_is_bounded_by_the_actual_protocol_frame_size()
    {
        var hello = ValidHello();
        hello.Inflight.Add(new AttemptRef
        {
            RunId = Guid.NewGuid().ToString(), NodeKey = new string('n', AgentRegistry.MaximumHelloMessageBytes),
            TaskKey = "t", Attempt = 1
        });
        var error = Assert.Throws<ArgumentException>(() => AgentRegistry.ValidateHello(hello, DateTimeOffset.UnixEpoch));
        Assert.Contains("message-size", error.Message);
    }

    private static Hello ValidHello() => new()
    {
        Id = Guid.NewGuid().ToString(), Name = "worker", Os = "linux", Arch = "x64", Cores = 4,
        RamMb = 8192, MaxTasks = 2, Pool = "default", ProtocolVersion = 1, Version = "1.0"
    };
}
