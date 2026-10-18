using System.Text.Json;
using Wf.Sdk;

namespace Wf.Sdk.Tests;

public sealed class NodeOutputsTests
{
    [Fact]
    public void OutputsPreserveTypesAndNulls()
    {
        var outputs = new NodeOutputs("node");
        outputs["text"] = "hello";
        outputs["number"] = 42;
        outputs["large"] = long.MaxValue;
        outputs["ratio"] = 1.5;
        outputs["flag"] = true;
        outputs["duration"] = TimeSpan.FromMinutes(2);
        outputs["list"] = new[] { "a", "b" };
        outputs["none"] = null;
        Assert.Equal("hello", outputs.Get<string>("text"));
        Assert.Equal(42, outputs.Get<int>("number"));
        Assert.Equal(long.MaxValue, outputs.Get<long>("large"));
        Assert.Equal(1.5, outputs.Get<double>("ratio"));
        Assert.True(outputs.Get<bool>("flag"));
        Assert.Equal(TimeSpan.FromMinutes(2), outputs.Get<TimeSpan>("duration"));
        Assert.Equal(["a", "b"], Assert.IsType<string[]>(outputs.Get<string[]>("list")));
        Assert.Null(outputs.Get<string>("none"));
    }

    [Fact]
    public void Exactly64KiBPassesAndOneExtraByteFailsWithoutLosingExistingValue()
    {
        var outputs = new NodeOutputs("build");
        var payload = new string('x', NodeOutputs.MaxBytes - 8);
        outputs["v"] = payload;
        Assert.Equal(NodeOutputs.MaxBytes, JsonSerializer.SerializeToUtf8Bytes(outputs.Values).Length);
        var error = Assert.Throws<InvalidOperationException>(() => outputs["v"] = payload + "x");
        Assert.Contains("build", error.Message);
        Assert.Contains("65537", error.Message);
        Assert.Contains("artifact", error.Message);
        Assert.Equal(payload, outputs.Get<string>("v"));
    }

    [Fact]
    public void LimitCountsAllKeysAndEscapedUtf8Bytes()
    {
        var outputs = new NodeOutputs("unicode");
        outputs["v"] = new string('\u00e9', 10_000);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(outputs.Values).Length > 60_000);
        Assert.Throws<InvalidOperationException>(() => outputs["second"] = new string('x', 6_000));
        Assert.Single(outputs.Values);
    }

    [Fact]
    public void JsonOutputsAreClonedAndSnapshotsStayImmutable()
    {
        var outputs = new NodeOutputs("node");
        using (var document = JsonDocument.Parse("{\"nested\":42}"))
            outputs["value"] = document.RootElement;
        var snapshot = outputs.Values;
        outputs["other"] = "later";
        Assert.Equal(42, outputs.Get<JsonElement>("value").GetProperty("nested").GetInt32());
        Assert.Single(snapshot);
    }

    [Fact]
    public async Task ConcurrentTasksCannotLoseEachOthersOutputs()
    {
        var outputs = new NodeOutputs("parallel");
        await Task.WhenAll(Enumerable.Range(0, 100).Select(i => Task.Run(() => outputs[$"v{i}"] = i)));
        Assert.Equal(100, outputs.Values.Count);
        for (var i = 0; i < 100; i++)
            Assert.Equal(i, outputs.Get<int>($"v{i}"));
    }

    [Fact]
    public void MissingAndInvalidKeysFailExplicitly()
    {
        var outputs = new NodeOutputs("node");
        Assert.Contains("node/missing", Assert.Throws<KeyNotFoundException>(() => outputs.Get<int>("missing")).Message);
        Assert.Throws<ArgumentException>(() => outputs[" "] = 1);
    }
}
