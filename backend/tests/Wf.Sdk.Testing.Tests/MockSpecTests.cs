using System.Collections.Immutable;
using System.Text.Json;
using Wf.Contracts.Definition;
using Wf.Sdk.Hosting;

namespace Wf.Sdk.Testing.Tests;

public sealed class MockSpecTests
{
    [Fact]
    public void Checkpoint_mock_clones_the_seed_and_round_trips_explicit_resume_execution()
    {
        MockSpec spec;
        var at = DateTimeOffset.UnixEpoch.AddDays(1);
        using (var document = JsonDocument.Parse("""{"position":41}"""))
            spec = new MockSpec(new TaskMock()).WithCheckpoint("node/work", new(document.RootElement, at, 7, 3));
        var wire = JsonSerializer.Deserialize(spec.ToJson(), DryRunJsonContext.Default.DryRunSpec)!;
        var mock = wire.Tasks!["node/work"];
        Assert.Equal(MockOutcome.ExecuteCheckpointed, mock.Outcome);
        Assert.Equal(41, mock.Checkpoint!.State.GetProperty("position").GetInt32());
        Assert.Equal(7, mock.Checkpoint.Sequence);
        Assert.Equal(3u, mock.Checkpoint.Attempt);
        Assert.Equal(at, mock.Checkpoint.SavedAt);
        Assert.ThrowsAny<ArgumentException>(() => new CheckpointMock(default, at));
        Assert.ThrowsAny<ArgumentException>(() => new CheckpointMock(JsonSerializer.SerializeToElement(1), at, 0));
        Assert.Throws<ArgumentNullException>(() => new MockSpec().WithCheckpoint("node/work", null!));
    }

    [Fact]
    public void FluentOutcomesUseTheExistingDryRunWireContract()
    {
        var outputs = new Dictionary<string, JsonElement> { ["answer"] = JsonSerializer.SerializeToElement(42) };
        var spec = new MockSpec()
            .Succeed("node/success", outputs)
            .Fail("node/failure", "failed", retryable: false, category: FailureCategory.AgentLost)
            .Timeout("node/timeout")
            .Throw("node/throw", "thrown")
            .FailNThenSucceed("node/retry", 2, outputs, "retry");
        var wire = JsonSerializer.Deserialize(spec.ToJson(), DryRunJsonContext.Default.DryRunSpec)!;
        Assert.Null(wire.Default);
        Assert.Empty(wire.Variables!);
        Assert.Equal(5, wire.Tasks!.Count);
        Assert.Equal(MockOutcome.Succeed, wire.Tasks["node/success"].Outcome);
        Assert.Equal(42, wire.Tasks["node/success"].Outputs!["answer"].GetInt32());
        Assert.Equal(MockOutcome.Fail, wire.Tasks["node/failure"].Outcome);
        Assert.Equal("failed", wire.Tasks["node/failure"].ErrorMessage);
        Assert.False(wire.Tasks["node/failure"].Retryable);
        Assert.Equal(FailureCategory.AgentLost, wire.Tasks["node/failure"].Category);
        Assert.Equal(MockOutcome.Timeout, wire.Tasks["node/timeout"].Outcome);
        Assert.Equal(MockOutcome.Throw, wire.Tasks["node/throw"].Outcome);
        Assert.Equal("thrown", wire.Tasks["node/throw"].ErrorMessage);
        Assert.Equal(MockOutcome.FailThenSucceed, wire.Tasks["node/retry"].Outcome);
        Assert.Equal(2, wire.Tasks["node/retry"].FailuresBeforeSuccess);
        Assert.Equal(42, wire.Tasks["node/retry"].Outputs!["answer"].GetInt32());
    }

    [Fact]
    public void SpecSnapshotsJsonValuesAndFluentChangesNeverMutateEarlierSpecs()
    {
        var original = new MockSpec();
        MockSpec spec;
        using (var document = JsonDocument.Parse("""{"payload":{"items":[1,true,null]},"env":"test"}"""))
        {
            var outputs = new Dictionary<string, JsonElement> { ["payload"] = document.RootElement.GetProperty("payload") };
            spec = original.Succeed("node/work", outputs)
                .WithVariable("env", document.RootElement.GetProperty("env"))
                .WithDefault(new TaskMock(Outputs: outputs.ToImmutableDictionary()));
            outputs.Clear();
        }
        var changed = spec.Fail("node/work").WithVariable("env", JsonSerializer.SerializeToElement("changed"));
        Assert.Empty(original.ToDryRunSpec().Tasks!);
        Assert.Null(original.ToDryRunSpec().Default);
        Assert.Empty(original.ToDryRunSpec().Variables!);
        Assert.Equal(MockOutcome.Succeed, spec.ToDryRunSpec().Tasks!["node/work"].Outcome);
        Assert.Equal(MockOutcome.Fail, changed.ToDryRunSpec().Tasks!["node/work"].Outcome);
        Assert.Equal("test", spec.ToDryRunSpec().Variables!["env"].GetString());
        Assert.Equal("changed", changed.ToDryRunSpec().Variables!["env"].GetString());
        Assert.Equal(3, spec.ToDryRunSpec().Tasks!["node/work"].Outputs!["payload"].GetProperty("items").GetArrayLength());
        Assert.Contains("\"payload\"", spec.ToJson());
    }

    [Fact]
    public void ConstructorSnapshotsAndValidatesExplicitDefault()
    {
        MockSpec spec;
        using (var document = JsonDocument.Parse("42"))
            spec = new MockSpec(new TaskMock(Outputs: ImmutableDictionary<string, JsonElement>.Empty.Add("value", document.RootElement)));
        Assert.Equal(42, spec.ToDryRunSpec().Default!.Outputs!["value"].GetInt32());
        Assert.Throws<ArgumentException>(() => new MockSpec(new TaskMock((MockOutcome)99)));
        Assert.Throws<ArgumentNullException>(() => new MockSpec().WithDefault(null!));
        Assert.Throws<ArgumentNullException>(() => new MockSpec().WithTask("node/work", null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("node")]
    [InlineData("/task")]
    [InlineData("node/")]
    [InlineData("node/ /task")]
    public void InvalidTaskKeysAreRejected(string key) =>
        Assert.ThrowsAny<ArgumentException>(() => new MockSpec().Succeed(key));

    [Fact]
    public void InvalidOutcomesMessagesCountsAndJsonValuesAreRejected()
    {
        var invalid = new[]
        {
            new TaskMock((MockOutcome)99), new TaskMock(Category: (FailureCategory)99),
            new TaskMock(FailuresBeforeSuccess: -1), new TaskMock(ErrorMessage: " "),
            new TaskMock(Outputs: ImmutableDictionary<string, JsonElement>.Empty.Add("", JsonSerializer.SerializeToElement(1))),
            new TaskMock(Outputs: ImmutableDictionary<string, JsonElement>.Empty.Add("value", default))
        };
        foreach (var mock in invalid)
            Assert.ThrowsAny<ArgumentException>(() => new MockSpec().WithTask("node/work", mock));
        Assert.ThrowsAny<ArgumentException>(() => new MockSpec().FailNThenSucceed("node/work", -1));
        Assert.ThrowsAny<ArgumentException>(() => new MockSpec().WithVariable(" ", JsonSerializer.SerializeToElement(1)));
        Assert.ThrowsAny<ArgumentException>(() => new MockSpec().WithVariable("value", default));
    }
}
