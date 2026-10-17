using System.Text.Json;
using System.Text.Json.Schema;
using Wf.Contracts;
using Wf.ControlPlane.Api.Machines;

namespace Wf.ControlPlane.Tests;

public sealed class MachineCapacityTests
{
    [Fact]
    public void Manual_capacity_uses_shared_canonical_profile_and_leaves_unsupplied_metadata_untouched()
    {
        using var document = JsonDocument.Parse("""
            {"manualCapacity":{"capacityRps":2147483647,"profile":{
                "payloadSizeClass":"small","codec":"json","transport":"http",
                "loadSettings":{"threads":"8","connections":"4"}}}}
            """);
        var patch = MachinePatchRequest.Parse(document.RootElement);
        Assert.Null(patch.Labels);
        Assert.Null(patch.Pool);
        Assert.Equal(int.MaxValue, patch.ManualCapacity!.CapacityRps);
        Assert.Equal("""{"transport":"http","codec":"json","payloadSizeClass":"small","loadSettings":{"connections":"4","threads":"8"}}""",
            patch.ManualCapacity.Profile.ProfileKey);
        Assert.NotNull(JsonSchemaExporter.GetJsonSchemaAsNode(MachineJsonContext.Default.ManualCapacityRequest));
        var profile = new CapacityProfile("http", "json", "small");
        var manual = new MachineCapacityResponse(profile, 100, "Manual", null);
        var json = JsonSerializer.Serialize(manual, MachineJsonContext.Default.MachineCapacityResponse);
        using var response = JsonDocument.Parse(json);
        Assert.Equal("Manual", response.RootElement.GetProperty("source").GetString());
        Assert.Equal(JsonValueKind.Null, response.RootElement.GetProperty("calibratedAt").ValueKind);
        Assert.False(response.RootElement.GetProperty("profile").TryGetProperty("profileKey", out _));
    }

    [Theory]
    [InlineData("""{"manualCapacity":null}""")]
    [InlineData("""{"manualCapacity":[]}""")]
    [InlineData("""{"manualCapacity":{}}""")]
    [InlineData("""{"manualCapacity":{"capacityRps":10}}""")]
    [InlineData("""{"manualCapacity":{"profile":null,"capacityRps":10}}""")]
    [InlineData("""{"manualCapacity":{"profile":{},"capacityRps":10}}""")]
    [InlineData("""{"manualCapacity":{"profile":{"transport":"http","codec":"json"},"capacityRps":10}}""")]
    [InlineData("""{"manualCapacity":{"profile":{"transport":" http","codec":"json","payloadSizeClass":"small"},"capacityRps":10}}""")]
    [InlineData("""{"manualCapacity":{"profile":{"transport":"http","codec":"json","payloadSizeClass":"small","extra":true},"capacityRps":10}}""")]
    [InlineData("""{"manualCapacity":{"profile":{"transport":"http","transport":"grpc","codec":"json","payloadSizeClass":"small"},"capacityRps":10}}""")]
    [InlineData("""{"manualCapacity":{"profile":{"transport":"http","codec":"json","payloadSizeClass":"small","loadSettings":{"x":1}},"capacityRps":10}}""")]
    [InlineData("""{"manualCapacity":{"profile":{"transport":"http","codec":"json","payloadSizeClass":"small","loadSettings":{"x":null}},"capacityRps":10}}""")]
    [InlineData("""{"manualCapacity":{"profile":{"transport":"http","codec":"json","payloadSizeClass":"small","loadSettings":{"x":"1","x":"2"}},"capacityRps":10}}""")]
    [InlineData("""{"manualCapacity":{"profile":{"transport":"http","codec":"json","payloadSizeClass":"small"},"capacityRps":0}}""")]
    [InlineData("""{"manualCapacity":{"profile":{"transport":"http","codec":"json","payloadSizeClass":"small"},"capacityRps":-1}}""")]
    [InlineData("""{"manualCapacity":{"profile":{"transport":"http","codec":"json","payloadSizeClass":"small"},"capacityRps":1.5}}""")]
    [InlineData("""{"manualCapacity":{"profile":{"transport":"http","codec":"json","payloadSizeClass":"small"},"capacityRps":2147483648}}""")]
    [InlineData("""{"manualCapacity":{"profile":{"transport":"http","codec":"json","payloadSizeClass":"small"},"capacityRps":"100"}}""")]
    [InlineData("""{"manualCapacity":{"profile":{"transport":"http","codec":"json","payloadSizeClass":"small"},"capacityRps":100,"source":"Calibrated"}}""")]
    [InlineData("""{"manualCapacity":{"profile":{"transport":"http","codec":"json","payloadSizeClass":"small"},"capacityRps":100,"capacityRps":200}}""")]
    [InlineData("""{"manualCapacity":{},"manualCapacity":{}}""")]
    public void Invalid_or_ambiguous_capacity_cannot_reach_persistence(string json)
    {
        using var document = JsonDocument.Parse(json);
        var error = Record.Exception(() => MachinePatchRequest.Parse(document.RootElement));
        Assert.True(error is ArgumentException or JsonException, $"Expected a validation failure, got {error?.GetType().Name ?? "success"}.");
    }

    [Fact]
    public void Shared_profile_bounds_are_enforced_even_when_metadata_in_the_same_patch_is_valid()
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            pool = "next",
            manualCapacity = new
            {
                capacityRps = 100,
                profile = new { transport = "http", codec = "json", payloadSizeClass = "small", loadSettings = new { payload = new string('\u2603', 400) } }
            }
        }));
        var error = Record.Exception(() => MachinePatchRequest.Parse(document.RootElement));
        Assert.True(error is ArgumentException or JsonException);
    }
}
