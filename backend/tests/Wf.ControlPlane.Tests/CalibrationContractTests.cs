using System.Text.Json;
using System.Text.Json.Schema;
using Wf.Contracts;
using Wf.ControlPlane.Api.Machines;
using Wf.ControlPlane.Engine.Agents;

namespace Wf.ControlPlane.Tests;

public sealed class CalibrationContractTests
{
    [Fact]
    public void Calibration_request_uses_the_shared_canonical_profile_without_result_or_executor_fields()
    {
        var request = JsonSerializer.Deserialize("""
            {"profile":{"transport":"http","codec":"json","payloadSizeClass":"small","loadSettings":{"threads":"8","connections":"4"}}}
            """, CalibrationJsonContext.Default.CalibrationRequest)!;
        Assert.Equal("""{"transport":"http","codec":"json","payloadSizeClass":"small","loadSettings":{"connections":"4","threads":"8"}}""",
            request.Profile.ProfileKey);
        Assert.NotNull(JsonSchemaExporter.GetJsonSchemaAsNode(CalibrationJsonContext.Default.CalibrationRequest));
        var record = new CalibrationRecord(Guid.CreateVersion7(), Guid.NewGuid(), request.Profile, 10, "Failed",
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(2), DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch, null, false, "Unsupported");
        var json = JsonSerializer.Serialize(record, CalibrationJsonContext.Default.CalibrationRecord);
        var restored = JsonSerializer.Deserialize(json, CalibrationJsonContext.Default.CalibrationRecord)!;
        Assert.Equal(record.Id, restored.Id);
        Assert.Equal(record.Profile.ProfileKey, restored.Profile.ProfileKey);
        Assert.Equal("Unsupported", restored.Error);
        Assert.Null(restored.CapacityRps);
        Assert.False(restored.Applied);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"profile":null}""")]
    [InlineData("""{"profile":{}}""")]
    [InlineData("""{"profile":{"transport":"http","codec":"json","payloadSizeClass":"small"},"capacityRps":100}""")]
    [InlineData("""{"profile":{"transport":"http","codec":"json","payloadSizeClass":"small"},"profile":{}}""")]
    [InlineData("""{"profile":{"transport":"http","codec":"json","payloadSizeClass":"small","source":"Manual"}}""")]
    public void Missing_ambiguous_or_result_shaped_calibration_requests_are_rejected(string json)
    {
        if (json == "null")
            Assert.Null(JsonSerializer.Deserialize(json, CalibrationJsonContext.Default.CalibrationRequest));
        else
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json, CalibrationJsonContext.Default.CalibrationRequest));
    }
}
