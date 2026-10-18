using Wf.Integration.Tests.Reports;

namespace Wf.Integration.Tests.Agents;

[Collection("Agent process containment")]
public sealed class AgentRestartTests(ReportPublishDatabase database) : IClassFixture<ReportPublishDatabase>
{
    [Theory]
    [InlineData("spool-running")]
    [InlineData("spool-completed")]
    public Task Real_agent_restart_preserves_original_delegate_result_all_logs_and_offline_resource_records(string mode) =>
        new HostSurvivalTests(database).RunAsync(mode);

    [Fact]
    public Task Host_enforces_hard_deadline_without_agent_and_replays_timeout_only_after_physical_tree_cleanup() =>
        new HostSurvivalTests(database).RunAsync("spool-deadline");
}
