namespace Wf.Chaos.Tests;

public sealed class ScheduleThroughputSmokeTests(ITestOutputHelper output)
{
    [Fact, Trait("Category", "ChaosSmoke")]
    public Task ScheduleChaosSmoke_three_real_minutes_is_not_the_one_hour_acceptance_gate() =>
        new ScheduleExactlyOnceTests(output).RunAsync(3, []);
}
