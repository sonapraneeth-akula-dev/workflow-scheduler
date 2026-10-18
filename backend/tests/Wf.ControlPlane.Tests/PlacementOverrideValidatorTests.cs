using Wf.Contracts.Definition;
using Wf.ControlPlane.Api;
using Wf.Engine.Core.Placement;
using System.Text.Json;
using Wf.Contracts.Runs;

namespace Wf.ControlPlane.Tests;

public sealed class PlacementOverrideValidatorTests
{
    private static readonly PlacementAgent Agent = new(Guid.NewGuid(), "machine", "windows", "test", 0, 4, 8192);

    [Fact]
    public void Omitted_placement_keeps_legacy_idempotency_request_shapes_but_explicit_disable_is_preserved()
    {
        var request = JsonSerializer.SerializeToElement(new TriggerRunRequest("team/type/name"),
            RunJsonContext.Default.TriggerRunRequest);
        Assert.False(request.TryGetProperty("placement", out _));
        var fingerprint = JsonSerializer.SerializeToElement(new RerunFingerprint(Guid.CreateVersion7(), null),
            RunJsonContext.Default.RerunFingerprint);
        Assert.Equal(["parentRunId", "fromNodes"], fingerprint.EnumerateObject().Select(property => property.Name));
        var rerun = JsonSerializer.SerializeToElement(new RerunRunRequest(Placement: new(false)),
            RunJsonContext.Default.RerunRunRequest);
        Assert.False(rerun.GetProperty("placement").GetProperty("singleMachine").GetBoolean());
    }

    [Fact]
    public void Combined_selectors_must_match_one_machine_and_multimachine_reservations_are_rejected()
    {
        Assert.True(PlacementOverrideValidator.Matches(Agent, new(true), [
            new("first", [], [new("task", placement: new(new(os: "windows")))]),
            new("second", [], [new("task", placement: new(new(pool: "test")))])]));
        Assert.False(PlacementOverrideValidator.Matches(Agent, new(true), [
            new("first", [], [new("task", placement: new(new(os: "windows")))]),
            new("second", [], [new("task", placement: new(new(os: "linux")))])]));
        Assert.False(PlacementOverrideValidator.Matches(Agent, new(true, "other"), [new("work", [], [new("task")])]));
        Assert.False(PlacementOverrideValidator.Matches(Agent, new(true), [
            new("work", [], [new("task")], placement: new(new(count: 2)))]));
        Assert.False(PlacementOverrideValidator.Matches(Agent, new(true), [
            new("work", [], [new("task", placement: new(new(count: 2)))])]));
    }

    [Theory]
    [InlineData(false, "machine", true)]
    [InlineData(true, "", true)]
    [InlineData(true, " ", true)]
    [InlineData(true, "machine\n", true)]
    [InlineData(false, null, false)]
    [InlineData(true, null, false)]
    [InlineData(true, "machine", false)]
    public void Override_contract_checks_names_and_explicit_disable(bool single, string? name, bool invalid) =>
        Assert.Equal(invalid, PlacementOverrideValidator.Invalid(new(single, name)) is not null);
}
