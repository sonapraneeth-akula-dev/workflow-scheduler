using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Wf.Contracts.Definition;
using Wf.Contracts.Manifest;
using Wf.Validation.SemVer;

namespace Wf.Validation.Tests;

public sealed class ApiBaselineTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void ManifestProjectionHasOnlyCanonicalPublicApi()
    {
        var manifest = Manifest(
            [new("z", VariableType.Int, true, Json("3")),
             new("environment", VariableType.Enum, allowedValues: ["prod", "dev"])],
            [new("z", [], [new("implementation")]), new("A", [], []), new("a", [], [])]);
        var baseline = ApiBaseline.FromManifest(manifest);
        var json = Encoding.UTF8.GetString(baseline.WriteUtf8());
        Assert.Equal("workflow.api.json", ApiBaseline.FileName);
        Assert.Equal("1.2.3-rc.1+build.004", baseline.Version);
        Assert.Equal(["environment", "z"], baseline.Variables.Select(variable => variable.Key));
        Assert.Equal(["dev", "prod"], baseline.Variables[0].AllowedValues);
        Assert.True(baseline.Variables[1].Required);
        Assert.Equal(3, baseline.Variables[1].Default!.Value.GetInt32());
        Assert.Equal(["A", "a", "z"], baseline.Nodes);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(["version", "variables", "nodes"],
            document.RootElement.EnumerateObject().Select(property => property.Name));
        var firstVariable = document.RootElement.GetProperty("variables")[0];
        Assert.Equal(["key", "type", "required", "default", "allowedValues"],
            firstVariable.EnumerateObject().Select(property => property.Name));
        Assert.Equal("Enum", firstVariable.GetProperty("type").GetString());
        Assert.DoesNotContain("implementation", json);
        Assert.DoesNotContain("sdkVersion", json);
        Assert.DoesNotContain("metadata", json);
        Assert.DoesNotContain("runTimeout", json);
        Assert.Equal(["prod", "dev"], manifest.Variables[1].AllowedValues);
    }

    [Fact]
    public void OrderingAndImplementationDetailsDoNotChangeBytes()
    {
        var first = Manifest(
            [new("z", VariableType.Int), new("a", VariableType.String, allowedValues: ["z", "A", "a"])],
            [new("z", [], []), new("a", [], [])]);
        var second = first with
        {
            SdkVersion = "different",
            Name = "different",
            Version = first.Version,
            Metadata = new("different", ImmutableSortedDictionary<string, string>.Empty),
            Variables = [new("a", VariableType.String, allowedValues: ["a", "z", "A"]), new("z", VariableType.Int)],
            Nodes = [new("a", [], [new("new-task")]), new("z", [new("a")], [])],
            Stages = ["a"],
            RunTimeout = TimeSpan.FromMinutes(1)
        };
        Assert.Equal(ApiBaseline.FromManifest(first).WriteUtf8(), ApiBaseline.FromManifest(second).WriteUtf8());
    }

    [Theory]
    [InlineData(VariableType.String, "\"hello\"")]
    [InlineData(VariableType.Int, "42")]
    [InlineData(VariableType.Long, "9223372036854775807")]
    [InlineData(VariableType.Double, "1.25")]
    [InlineData(VariableType.Bool, "false")]
    [InlineData(VariableType.Enum, "\"Dev\"")]
    [InlineData(VariableType.StringList, "[\"b\",\"a\"]")]
    [InlineData(VariableType.Duration, "\"1.02:03:04\"")]
    [InlineData(VariableType.Secret, "{\"Name\":\"deploy-token\"}")]
    public void RoundTripPreservesVariableContractsAndOwnedDefaults(VariableType type, string value)
    {
        ApiBaseline baseline;
        using (var document = JsonDocument.Parse(value))
            baseline = ApiBaseline.FromManifest(Manifest(
                [new("input", type, true, document.RootElement, type == VariableType.Enum ? ["Prod", "Dev"] : [])]));
        var bytes = baseline.WriteUtf8();
        var parsed = ApiBaseline.Parse(bytes);
        Assert.Equal(bytes, parsed.WriteUtf8());
        var variable = Assert.Single(parsed.Variables);
        Assert.Equal("input", variable.Key);
        Assert.Equal(type, variable.Type);
        Assert.True(variable.Required);
        Assert.True(JsonElement.DeepEquals(Json(value), variable.Default!.Value));
        if (type == VariableType.Enum)
            Assert.Equal(["Dev", "Prod"], variable.AllowedValues);
    }

    [Fact]
    public void ParsingCanonicalizesOrderingAndKeepsMissingDefaultDistinctFromValue()
    {
        var baseline = Parse("""
            {"nodes":["z","a"],"variables":[
                {"key":"z","type":"Bool","default":false},
                {"key":"a","type":"String","required":true}],"version":"1.0.0"}
            """);
        Assert.Equal(["a", "z"], baseline.Nodes);
        Assert.Null(baseline.Variables[0].Default);
        Assert.True(baseline.Variables[0].Required);
        Assert.False(baseline.Variables[1].Default!.Value.GetBoolean());
        Assert.Equal(baseline.WriteUtf8(), ApiBaseline.Parse(baseline.WriteUtf8()).WriteUtf8());
    }

    [Theory]
    [InlineData("0.0.0")]
    [InlineData("1.2.3")]
    [InlineData("1.2.3-alpha.0.x-y+001.sha-abc")]
    [InlineData("1.2.3-0")]
    [InlineData("1.2.3-00a")]
    [InlineData("1.2.3--")]
    [InlineData("999999999999999999999999999999.0.0")]
    public void AcceptsSemVerTwoWithoutIntegerSizeLimit(string version) =>
        Assert.Equal(version, new ApiBaseline(version, [], []).Version);

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("1")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("v1.2.3")]
    [InlineData("01.2.3")]
    [InlineData("1.02.3")]
    [InlineData("1.2.03")]
    [InlineData("1.2.3-01")]
    [InlineData("1.2.3-a.00")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-a..b")]
    [InlineData("1.2.3+")]
    [InlineData("1.2.3+a..b")]
    [InlineData("1.2.3+a+b")]
    [InlineData("1.2.3-a_b")]
    [InlineData("1.2.3\n")]
    [InlineData("١.2.3")]
    public void RejectsNonSemVerVersions(string version) =>
        Assert.Throws<ArgumentException>(() => new ApiBaseline(version, [], []));

    [Theory]
    [InlineData("")]
    [InlineData("not JSON")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"variables":[],"nodes":[]}""")]
    [InlineData("""{"version":"1.0.0","nodes":[]}""")]
    [InlineData("""{"version":"1.0.0","variables":[]}""")]
    [InlineData("""{"version":null,"variables":[],"nodes":[]}""")]
    [InlineData("""{"version":"1.0.0","variables":null,"nodes":[]}""")]
    [InlineData("""{"version":"1.0.0","variables":[],"nodes":null}""")]
    [InlineData("""{"version":"1","variables":[],"nodes":[]}""")]
    [InlineData("""{"version":"1.0.0","variables":[],"nodes":[],"extra":"do-not-echo"}""")]
    [InlineData("""{"version":"1.0.0","version":"2.0.0","variables":[],"nodes":[]}""")]
    [InlineData("""{"version":"1.0.0","variables":[],"nodes":["a","a"]}""")]
    [InlineData("""{"version":"1.0.0","variables":[],"nodes":[null]}""")]
    [InlineData("""{"version":"1.0.0","variables":[],"nodes":[" "]}""")]
    [InlineData("""{"version":"1.0.0","variables":[],"nodes":[{"key":"a"}]}""")]
    [InlineData("""{"version":"1.0.0","variables":[null],"nodes":[]}""")]
    [InlineData("""{"version":"1.0.0","variables":[{},{}],"nodes":[]}""")]
    [InlineData("""{"version":"1.0.0","variables":[{"key":"a","type":"String"},{"key":"a","type":"Int"}],"nodes":[]}""")]
    [InlineData("""{"version":"1.0.0","variables":[],"nodes":[],}""")]
    [InlineData("""{"version":"1.0.0","variables":[],"nodes":[]} {}""")]
    public void InvalidDocumentsAreRejectedWithoutEchoingInputs(string json) => AssertInvalid(json);

    [Theory]
    [InlineData("""{"key":"input"}""")]
    [InlineData("""{"key":" ","type":"String"}""")]
    [InlineData("""{"key":null,"type":"String"}""")]
    [InlineData("""{"key":"input","type":"Unknown"}""")]
    [InlineData("""{"key":"input","type":999}""")]
    [InlineData("""{"key":"input","type":"String","required":null}""")]
    [InlineData("""{"key":"input","type":"String","required":"true"}""")]
    [InlineData("""{"key":"input","type":"String","type":"Int"}""")]
    [InlineData("""{"key":"input","type":"Int","default":"do-not-echo"}""")]
    [InlineData("""{"key":"input","type":"Double","default":1e999}""")]
    [InlineData("""{"key":"input","type":"StringList","default":[null]}""")]
    [InlineData("""{"key":"input","type":"Duration","default":"do-not-echo"}""")]
    [InlineData("""{"key":"input","type":"Enum","allowedValues":[]}""")]
    [InlineData("""{"key":"input","type":"Enum","allowedValues":["dev"],"default":"do-not-echo"}""")]
    [InlineData("""{"key":"input","type":"Int","allowedValues":["1"]}""")]
    [InlineData("""{"key":"input","type":"String","allowedValues":[null]}""")]
    [InlineData("""{"key":"input","type":"String","allowedValues":["a","a"]}""")]
    [InlineData("""{"key":"input","type":"String","allowedValues":null}""")]
    [InlineData("""{"key":"input","type":"String","extra":"do-not-echo"}""")]
    [InlineData("""{"key":"input","type":"Secret","default":"do-not-echo"}""")]
    [InlineData("""{"key":"input","type":"Secret","default":{"Name":"token","Value":"do-not-echo"}}""")]
    [InlineData("""{"key":"input","type":"Secret","default":{"Name":"token","Name":"do-not-echo"}}""")]
    [InlineData("""{"key":"input","type":"Secret","default":{"Name":" "}}""")]
    [InlineData("""{"key":"input","type":"Secret","default":{"name":"token"}}""")]
    public void UntrustedVariablesCannotBypassExistingContracts(string variable) =>
        AssertInvalid($$"""{"version":"1.0.0","variables":[{{variable}}],"nodes":[]}""");

    [Fact]
    public void TypedInputsRejectUninitializedCollectionsNullsAndDuplicates()
    {
        Assert.Throws<ArgumentException>(() => new ApiBaseline("1.0.0", default, []));
        Assert.Throws<ArgumentException>(() => new ApiBaseline("1.0.0", [], default));
        Assert.Throws<ArgumentException>(() => new ApiBaseline("1.0.0", [null!], []));
        Assert.Throws<ArgumentException>(() => new ApiBaseline("1.0.0", [], [null!]));
        Assert.Throws<ArgumentException>(() => new ApiBaseline("1.0.0", [], ["a", "a"]));
        var variable = new VariableSchema("a", VariableType.String);
        Assert.Throws<ArgumentException>(() => new ApiBaseline("1.0.0", [variable, variable], []));
        Assert.Throws<ArgumentNullException>(() => ApiBaseline.FromManifest(null!));
        Assert.Throws<ArgumentException>(() => ApiBaseline.FromManifest(Manifest() with { Nodes = default }));
        Assert.Throws<ArgumentException>(() => ApiBaseline.FromManifest(Manifest() with { Nodes = [null!] }));
    }

    [Fact]
    public async Task AsyncRoundTripLeavesCallerStreamsOpen()
    {
        var baseline = ApiBaseline.FromManifest(Manifest([new("token", VariableType.Secret, true, Json("""{"Name":"ref"}"""))]));
        using var stream = new MemoryStream();
        await baseline.WriteAsync(stream, Token);
        Assert.True(stream.CanWrite);
        Assert.Equal(baseline.WriteUtf8(), stream.ToArray());
        stream.Position = 0;
        var parsed = await ApiBaseline.ReadAsync(stream, Token);
        Assert.True(stream.CanRead);
        Assert.Equal(baseline.WriteUtf8(), parsed.WriteUtf8());
    }

    [Fact]
    public async Task AsyncParsingSanitizesInvalidContractErrorsToo()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("""
            {"version":"1.0.0","variables":[{"key":"do-not-echo","type":"Secret","default":"do-not-echo"}],"nodes":[]}
            """));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => ApiBaseline.ReadAsync(stream, Token));
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("do-not-echo", error.ToString());
    }

    [Fact]
    public async Task CancellationAndInfrastructureErrorsPropagateUnchanged()
    {
        var baseline = new ApiBaseline("1.0.0", [], []);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var memory = new MemoryStream(baseline.WriteUtf8());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ApiBaseline.ReadAsync(memory, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => baseline.WriteAsync(memory, cancelled.Token));
        using var failure = new FailingStream();
        Assert.Same(failure.Error, await Assert.ThrowsAsync<IOException>(() => ApiBaseline.ReadAsync(failure, Token)));
        Assert.Same(failure.Error, await Assert.ThrowsAsync<IOException>(() => baseline.WriteAsync(failure, Token)));
    }

    [Fact]
    public async Task CancellationDuringIoPropagatesAndLeavesStreamsOpen()
    {
        var baseline = new ApiBaseline("1.0.0", [], []);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        using var readStream = new BlockingStream();
        using var writeStream = new BlockingStream();
        var read = ApiBaseline.ReadAsync(readStream, cancellation.Token);
        var write = baseline.WriteAsync(writeStream, cancellation.Token);
        Assert.False(read.IsCompleted);
        Assert.False(write.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
        Assert.True(readStream.CanRead);
        Assert.True(writeStream.CanWrite);
    }

    private static WorkflowManifest Manifest(ImmutableArray<VariableSchema> variables = default,
        ImmutableArray<NodeDefinition> nodes = default) =>
        new("1.0.0", "baseline-test", "1.2.3-rc.1+build.004",
            new("release", ImmutableSortedDictionary<string, string>.Empty),
            variables.IsDefault ? [] : variables, [], nodes.IsDefault ? [] : nodes, TimeSpan.FromHours(1));

    private static ApiBaseline Parse(string json) => ApiBaseline.Parse(Encoding.UTF8.GetBytes(json));

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static void AssertInvalid(string json)
    {
        var error = Assert.Throws<InvalidDataException>(() => Parse(json));
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("do-not-echo", error.ToString());
    }

    private sealed class FailingStream : MemoryStream
    {
        internal IOException Error { get; } = new("Test storage failure.");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(Error);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(Error);
    }

    private sealed class BlockingStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            await Task.Delay(Timeout.Infinite, cancellationToken);
    }
}
