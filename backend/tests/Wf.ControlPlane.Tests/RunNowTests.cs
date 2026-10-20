using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using Wf.Contracts.Definition;
using Wf.ControlPlane.Engine.Recovery;
using Wf.Engine.Core;

namespace Wf.ControlPlane.Tests;

public sealed class RunNowTests
{
    [Fact]
    public void Command_round_trip_preserves_permanent_series_and_legacy_payloads_default_to_no_series()
    {
        var run = new RunState(Guid.CreateVersion7(), [new("work", [], [new("task")])],
            new WorkflowMetadata("release", ImmutableSortedDictionary<string, string>.Empty), Assert.Fail);
        var series = Guid.CreateVersion7();
        var command = new TriggerRunCommand(run.Snapshot, "team/release/workflow", Guid.CreateVersion7(),
            DateTimeOffset.UtcNow.AddHours(1), series) { TriggeredByName = "Original operator" };
        var json = JsonSerializer.Serialize(command);
        Assert.Equal(series, JsonSerializer.Deserialize<TriggerRunCommand>(json)!.SeriesId);
        Assert.Equal("Original operator", JsonSerializer.Deserialize<TriggerRunCommand>(json)!.TriggeredByName);
        var legacy = JsonSerializer.SerializeToElement(command);
        var properties = legacy.EnumerateObject().Where(property => property.Name is not ("SeriesId" or "TriggeredByName"))
            .ToDictionary(property => property.Name, property => property.Value);
        var restored = JsonSerializer.Deserialize<TriggerRunCommand>(JsonSerializer.Serialize(properties))!;
        Assert.Null(restored.SeriesId);
        Assert.Null(restored.TriggeredByName);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("not\0persistable")]
    public async Task Invalid_recorded_actor_names_fail_before_any_command_is_persisted(string name)
    {
        using var source = NpgsqlDataSource.Create("Host=localhost;Database=unused");
        var run = new RunState(Guid.CreateVersion7(), [], new("release", ImmutableSortedDictionary<string, string>.Empty), Assert.Fail);
        var command = new TriggerRunCommand(run.Snapshot, "team/release/workflow", Guid.CreateVersion7(),
            DateTimeOffset.UtcNow.AddHours(1)) { TriggeredByName = name };
        var inbox = new CommandInbox(source, TimeProvider.System);
        await Assert.ThrowsAsync<ArgumentException>(() => inbox.TriggerAsync(command, "actor", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Empty_series_identity_fails_before_any_command_is_persisted()
    {
        using var source = NpgsqlDataSource.Create("Host=localhost;Database=unused");
        var run = new RunState(Guid.CreateVersion7(), [], new("release", ImmutableSortedDictionary<string, string>.Empty), Assert.Fail);
        var inbox = new CommandInbox(source, TimeProvider.System);
        await Assert.ThrowsAsync<ArgumentException>(() => inbox.TriggerAsync(new(run.Snapshot,
            "team/release/workflow", Guid.CreateVersion7(), DateTimeOffset.UtcNow.AddHours(1), Guid.Empty),
            "actor", TestContext.Current.CancellationToken));
    }
}
