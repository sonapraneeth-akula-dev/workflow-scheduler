using System.Text.Json;
using Wf.Sdk.Hosting;

namespace Wf.Sdk.Testing;

/// <summary>Opt-in resume seed. The named real delegate executes; checkpoint saves remain unsupported in dry-run.</summary>
public sealed class CheckpointMock
{
    internal CheckpointMockData Seed { get; }

    public CheckpointMock(JsonElement state, DateTimeOffset savedAt, long sequence = 1, uint attempt = 2)
    {
        var seed = new CheckpointMockData(state, sequence, savedAt, attempt);
        DryRunHost.ValidateMock("checkpoint", new(MockOutcome.ExecuteCheckpointed, Checkpoint: seed));
        Seed = seed with { State = state.Clone() };
    }
}
