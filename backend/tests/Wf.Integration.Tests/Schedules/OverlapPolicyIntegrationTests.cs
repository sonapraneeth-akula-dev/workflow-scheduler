using System.Text.Json;
using Wf.Contracts.Schedules;
using Wf.Engine.Core.Scheduling;

namespace Wf.Integration.Tests.Schedules;

public sealed class OverlapPolicyIntegrationTests
{
    [Theory]
    [InlineData("Skip", ScheduleOverlapDecision.Skip)]
    [InlineData("Queue", ScheduleOverlapDecision.Queue)]
    [InlineData("Concurrent", ScheduleOverlapDecision.Start)]
    public void Persisted_code_schedule_uses_the_same_core_policy_after_restart(string policy, ScheduleOverlapDecision expected)
    {
        var json = $$"""{"instances":[{"id":"recurring","timezone":"UTC","cron":"* * * * *","overlap":"{{policy}}"}]}""";
        var original = JsonSerializer.Deserialize(json, ScheduleFileJsonContext.Default.ScheduleFile)!;
        var persisted = JsonSerializer.Serialize(original, ScheduleFileJsonContext.Default.ScheduleFile);
        var restored = JsonSerializer.Deserialize(persisted, ScheduleFileJsonContext.Default.ScheduleFile)!;
        var schedule = Assert.Single(restored.Instances);
        Assert.Equal(ScheduleOverlapDecision.Start, OverlapPolicy.Decide(0, schedule.Overlap));
        Assert.Equal(expected, OverlapPolicy.Decide(1, schedule.Overlap));
        Assert.Equal(expected, OverlapPolicy.Decide(2, schedule.Overlap));
        Assert.Equal(policy, schedule.Overlap.ToString());
        Assert.Equal(policy, JsonSerializer.SerializeToElement(original, ScheduleFileJsonContext.Default.ScheduleFile)
            .GetProperty("instances")[0].GetProperty("overlap").GetString());
    }

    [Fact]
    public void Omitted_overlap_defaults_to_skip_without_preventing_first_run()
    {
        var file = JsonSerializer.Deserialize(
            """{"instances":[{"id":"default","timezone":"UTC","cron":"* * * * *"}]}""",
            ScheduleFileJsonContext.Default.ScheduleFile)!;
        var schedule = Assert.Single(file.Instances);
        Assert.Equal(ScheduleOverlapPolicy.Skip, schedule.Overlap);
        Assert.Equal(ScheduleOverlapDecision.Start, OverlapPolicy.Decide(0, schedule.Overlap));
        Assert.Equal(ScheduleOverlapDecision.Skip, OverlapPolicy.Decide(1, schedule.Overlap));
    }
}
