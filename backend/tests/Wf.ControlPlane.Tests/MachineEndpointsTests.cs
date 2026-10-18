using System.Text.Json;
using System.Text.Json.Schema;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Wf.ControlPlane.Api.Machines;

namespace Wf.ControlPlane.Tests;

public sealed class MachineEndpointsTests
{
    [Fact]
    public void Machine_filters_default_to_all_and_combine_exact_metadata_constraints()
    {
        Assert.Equal(new MachineQuery(null, null, null, []), MachineQuery.Parse(new QueryCollection()));
        var query = MachineQuery.Parse(new QueryCollection(new Dictionary<string, StringValues>
        {
            ["pool"] = "test", ["os"] = "linux", ["status"] = "Healthy",
            ["label"] = new(["region=eu", "token=a=b"])
        }));
        Assert.Equal("test", query.Pool);
        Assert.Equal((short)1, query.Os);
        Assert.Equal((short)0, query.Status);
        Assert.Equal(["region=eu", "token=a=b"], query.Labels);
        Assert.NotNull(JsonSchemaExporter.GetJsonSchemaAsNode(MachineJsonContext.Default.MachineListResponse));
        Assert.NotNull(JsonSchemaExporter.GetJsonSchemaAsNode(MachineJsonContext.Default.MachinePatchRequest));
        Assert.Equal("""{"items":[]}""",
            JsonSerializer.Serialize(new MachineListResponse([]), MachineJsonContext.Default.MachineListResponse));
    }

    [Theory]
    [InlineData("os", "Linux")]
    [InlineData("os", "macos")]
    [InlineData("os", "1")]
    [InlineData("status", "healthy")]
    [InlineData("status", "0")]
    [InlineData("status", "Unknown")]
    [InlineData("status", "Healthy,Draining")]
    [InlineData("label", "region")]
    [InlineData("label", "=value")]
    [InlineData("label", "region=")]
    [InlineData("label", "region=\nvalue")]
    [InlineData("pool", "")]
    [InlineData("pool", " ")]
    [InlineData("pool", "bad\npool")]
    [InlineData("Pool", "default")]
    [InlineData("unknown", "value")]
    public void Machine_filters_reject_unknown_malformed_or_noncanonical_values(string key, string value) =>
        Assert.Throws<ArgumentException>(() => MachineQuery.Parse(new QueryCollection(
            new Dictionary<string, StringValues> { [key] = value })));

    [Theory]
    [InlineData("pool", "a", "b")]
    [InlineData("os", "linux", "windows")]
    [InlineData("status", "Healthy", "Unhealthy")]
    [InlineData("label", "key=one", "key=two")]
    [InlineData("label", "key=one", "key=one")]
    public void Machine_filters_reject_duplicate_scalars_and_label_keys(string key, string first, string second) =>
        Assert.Throws<ArgumentException>(() => MachineQuery.Parse(new QueryCollection(
            new Dictionary<string, StringValues> { [key] = new([first, second]) })));

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("""{"labels":null}""")]
    [InlineData("""{"pool":null}""")]
    [InlineData("""{"pool":42}""")]
    [InlineData("""{"pool":""}""")]
    [InlineData("""{"pool":"bad\npool"}""")]
    [InlineData("""{"labels":[]}""")]
    [InlineData("""{"labels":{"x":3}}""")]
    [InlineData("""{"labels":{"x":null}}""")]
    [InlineData("""{"labels":{"": "x"}}""")]
    [InlineData("""{"labels":{"x=y": "z"}}""")]
    [InlineData("""{"labels":{"x": ""}}""")]
    [InlineData("""{"labels":{"x":"one","x":"two"}}""")]
    [InlineData("""{"pool":"one","pool":"two"}""")]
    [InlineData("""{"labels":{},"labels":{}}""")]
    [InlineData("""{"Labels":{}}""")]
    [InlineData("""{"status":"Healthy"}""")]
    [InlineData("""{"pool":"test","unknown":true}""")]
    public void Machine_patch_rejects_ambiguous_unknown_null_or_invalid_metadata(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Throws<ArgumentException>(() => MachinePatchRequest.Parse(document.RootElement));
    }

    [Fact]
    public void Machine_patch_preserves_omitted_fields_and_accepts_empty_full_replacement_labels()
    {
        using var labels = JsonDocument.Parse("""{"labels":{}}""");
        var clear = MachinePatchRequest.Parse(labels.RootElement);
        Assert.Empty(clear.Labels!);
        Assert.Null(clear.Pool);
        using var pool = JsonDocument.Parse("""{"pool":"new"}""");
        var move = MachinePatchRequest.Parse(pool.RootElement);
        Assert.Null(move.Labels);
        Assert.Equal("new", move.Pool);
        using var both = JsonDocument.Parse("""{"pool":"new","labels":{"region":"eu","token":"a=b"}}""");
        Assert.Equal("a=b", MachinePatchRequest.Parse(both.RootElement).Labels!["token"]);
    }

    [Fact]
    public void Machine_metadata_bounds_are_shared_with_registration_and_filters()
    {
        foreach (var labels in new[]
        {
            new Dictionary<string, string> { [new string('k',101)] = "v" },
            new Dictionary<string, string> { ["k"] = new('v',201) },
            Enumerable.Range(0,101).ToDictionary(index => "k" + index, _ => "v")
        })
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { labels }));
            Assert.Throws<ArgumentException>(() => MachinePatchRequest.Parse(document.RootElement));
        }
        using var pool = JsonDocument.Parse(JsonSerializer.Serialize(new { pool = new string('p', 201) }));
        Assert.Throws<ArgumentException>(() => MachinePatchRequest.Parse(pool.RootElement));
    }
}
