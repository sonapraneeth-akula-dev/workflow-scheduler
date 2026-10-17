using System.Collections.Immutable;
using System.Text.Json;
using Wf.Contracts.Runs;

namespace Wf.ControlPlane.Tests;

public sealed class RerunEndpointTests
{
    [Theory]
    [InlineData("null")]
    [InlineData("{\"fromNodes\":42}")]
    [InlineData("{\"fromNodes\":[42]}")]
    [InlineData("{\"fromNodes\":[],\"variables\":{}}")]
    [InlineData("{\"version\":\"1.0.0\"}")]
    [InlineData("{\"workflowId\":\"another/type/workflow\"}")]
    [InlineData("{\"fromNodes\":[],\"fromNodes\":[]}")]
    public void Rerun_request_rejects_unknown_duplicate_and_wrongly_typed_properties(string json)
    {
        if (json == "null")
            Assert.Null(JsonSerializer.Deserialize(json, RunJsonContext.Default.RerunRunRequest));
        else
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json, RunJsonContext.Default.RerunRunRequest));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"fromNodes\":null}")]
    public void Omitted_or_null_fromNodes_selects_default_terminal_rerun_behavior(string json) =>
        Assert.Null(JsonSerializer.Deserialize(json, RunJsonContext.Default.RerunRunRequest)!.FromNodes);

    [Fact]
    public void Shared_rerun_contract_retains_exact_node_keys_and_response_UUID()
    {
        var request = JsonSerializer.Deserialize("{\"fromNodes\":[\"deploy-US\",\"check\"]}",
            RunJsonContext.Default.RerunRunRequest)!;
        Assert.Equal(["deploy-US", "check"], request.FromNodes!.Value);
        var parent = Guid.CreateVersion7();
        var fingerprint = JsonSerializer.Serialize(new RerunFingerprint(parent, request.FromNodes),
            RunJsonContext.Default.RerunFingerprint);
        using var json = JsonDocument.Parse(fingerprint);
        Assert.Equal(parent, json.RootElement.GetProperty("parentRunId").GetGuid());
        Assert.Equal(2, json.RootElement.GetProperty("fromNodes").GetArrayLength());
        var run = Guid.CreateVersion7();
        var response = JsonSerializer.Serialize(new TriggerRunResponse(run), RunJsonContext.Default.TriggerRunResponse);
        Assert.Equal(run, JsonSerializer.Deserialize(response, RunJsonContext.Default.TriggerRunResponse)!.RunId);
    }

    [Fact]
    public void Rerun_trigger_does_not_renumber_existing_history_values()
    {
        Assert.Equal(0, (short)RunTrigger.Manual);
        Assert.Equal(1, (short)RunTrigger.Schedule);
        Assert.Equal(2, (short)RunTrigger.CatchUp);
        Assert.Equal(3, (short)RunTrigger.Rerun);
    }
}
