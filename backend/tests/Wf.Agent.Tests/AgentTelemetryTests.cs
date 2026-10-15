using System.Runtime.InteropServices;

namespace Wf.Agent.Tests;

public sealed class AgentTelemetryTests
{
    [Fact]
    public void Hello_reports_real_machine_and_configured_identity()
    {
        var telemetry = new AgentTelemetry(TimeProvider.System);
        var options = new AgentOptions([new Uri("http://localhost:5000")], "id", "name", "ci", 2,
            new Dictionary<string, string> { ["dotnet"] = "10" });
        var hello = telemetry.CreateHello(options);
        Assert.Equal("id", hello.Id);
        Assert.Equal("name", hello.Name);
        Assert.Equal("ci", hello.Pool);
        Assert.Equal(2U, hello.MaxTasks);
        Assert.Equal((uint)Environment.ProcessorCount, hello.Cores);
        Assert.Equal(OperatingSystem.IsWindows() ? "windows" : "linux", hello.Os);
        Assert.Equal(RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(), hello.Arch);
        Assert.True(hello.RamMb > 0);
        Assert.NotEmpty(hello.Version);
        Assert.Equal(1U, hello.ProtocolVersion);
        Assert.Equal("10", hello.Labels["dotnet"]);
    }

    [Fact]
    public void Heartbeat_reports_bounded_cpu_memory_disk_and_utc_time()
    {
        var telemetry = new AgentTelemetry(TimeProvider.System);
        var start = DateTimeOffset.UtcNow;
        var first = telemetry.Sample();
        var second = telemetry.Sample();
        Assert.InRange(first.CpuPercent, 0, 100);
        Assert.InRange(second.CpuPercent, 0, 100);
        Assert.True(first.RamUsedMb > 0);
        Assert.True(first.DiskFreeMb > 0);
        Assert.InRange(first.At.ToDateTimeOffset(), start, DateTimeOffset.UtcNow);
        Assert.Equal(0U, first.SlotsUsed);
    }
}
