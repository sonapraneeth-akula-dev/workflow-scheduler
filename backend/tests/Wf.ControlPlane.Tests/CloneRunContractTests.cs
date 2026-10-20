using System.Text.Json;
using Wf.Contracts.Runs;

namespace Wf.ControlPlane.Tests;

public sealed class CloneRunContractTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("""{"variables":null}""")]
    [InlineData("""{"variables":[]}""")]
    [InlineData("""{"variables":{},"fromNodes":["build"]}""")]
    [InlineData("""{"variables":{},"version":"2.0.0"}""")]
    [InlineData("""{"variables":{},"variables":{}}""")]
    [InlineData("""{"variables":{"one":1,"one":2}}""")]
    public void Clone_requires_an_unambiguous_variable_object_and_cannot_change_pin_or_select_partial_nodes(string json) =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json, RunJsonContext.Default.CloneRunRequest));

    [Fact]
    public void Clone_JSON_preserves_nested_secret_references_arrays_and_large_number_tokens()
    {
        const string source = """{"variables":{"limit":9223372036854775807,"secret":{"Name":"secret://vault/api/token"},"regions":["us","eu"],"flag":true}}""";
        var request = JsonSerializer.Deserialize(source, RunJsonContext.Default.CloneRunRequest)!;
        Assert.Equal(long.MaxValue, request.Variables["limit"].GetInt64());
        Assert.Equal("secret://vault/api/token", request.Variables["secret"].GetProperty("Name").GetString());
        Assert.Equal(["us", "eu"], request.Variables["regions"].EnumerateArray().Select(value => value.GetString()));
        var roundtrip = JsonSerializer.Serialize(request, RunJsonContext.Default.CloneRunRequest);
        Assert.Contains("9223372036854775807", roundtrip);
        Assert.Contains("\"flag\":true", roundtrip);
    }
}
