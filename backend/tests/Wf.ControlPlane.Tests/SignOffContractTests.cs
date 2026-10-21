using System.Text.Json;
using Wf.Contracts.Runs;
using Wf.ControlPlane.Api;

namespace Wf.ControlPlane.Tests;

public sealed class SignOffContractTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("""{"reason":null}""")]
    [InlineData("""{"reason":1}""")]
    [InlineData("""{"reason":"reviewed","actorName":"spoof"}""")]
    [InlineData("""{"reason":"first","reason":"second"}""")]
    [InlineData("""{"reason":"reviewed","versionId":"spoof"}""")]
    public void SignOff_requires_one_reason_and_rejects_client_identity_or_version(string json) =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json, SignOffJsonContext.Default.SignOffRequest));

    [Theory]
    [InlineData("0", true)]
    [InlineData("1", true)]
    [InlineData("9223372036854775807", true)]
    [InlineData("01", false)]
    [InlineData("-1", false)]
    [InlineData("+1", false)]
    [InlineData(" 1", false)]
    [InlineData("1.0", false)]
    [InlineData("9223372036854775808", false)]
    [InlineData("", false)]
    public void SignOff_revision_is_canonical_nonnegative_int64(string value, bool expected) =>
        Assert.Equal(expected, SignOffEndpoints.TryRevision(value, out _));
}
