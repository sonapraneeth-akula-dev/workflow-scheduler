using System.Text;
using Google.Protobuf;
using Wf.Agent.Execution;
using Wf.Agent.Secrets;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Outputs;

namespace Wf.Agent.Tests;

public sealed class TaskOutputsTests
{
    private static TaskCompleted Result(byte[] bytes) => new()
    {
        Outcome = TaskOutcome.Succeeded, OutputsJson = ByteString.CopyFrom(bytes)
    };

    [Theory]
    [InlineData(65535, true)]
    [InlineData(65536, true)]
    [InlineData(65537, false)]
    public void TaskOutputs_counts_exact_serialized_UTF8_bytes(int size, bool valid)
    {
        var text = "{\"v\":\"" + new string('é', (size - 8) / 2) + ((size - 8) % 2 == 1 ? "x" : "") + "\"}";
        var bytes = Encoding.UTF8.GetBytes(text);
        Assert.Equal(size, bytes.Length);
        var result = OutputsGuard.Apply(Result(bytes), new([]));
        Assert.Equal(valid ? TaskOutcome.Succeeded : TaskOutcome.Failed, result.Outcome);
        if (valid) Assert.Equal(bytes, result.OutputsJson.ToByteArray());
        else
        {
            Assert.Equal(TaskOutputJson.SizeError, result.Error.Message);
            Assert.True(result.Error.NonRetryable);
            Assert.Empty(result.OutputsJson);
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"payload\"")]
    [InlineData("{broken secret-content")]
    [InlineData("{\"x\":1,\"x\":2}")]
    [InlineData("{\"x\":{\"y\":1,\"y\":2}}")]
    [InlineData("{\"x\":\"\\uD800\"}")]
    public void TaskOutputs_invalid_producer_JSON_fails_with_safe_error(string json)
    {
        var original = Result(Encoding.UTF8.GetBytes(json));
        var result = OutputsGuard.Apply(original, new([]));
        Assert.Equal(TaskOutcome.Failed, result.Outcome);
        Assert.Equal("ValidationError", result.Error.Category);
        Assert.Equal(TaskOutputJson.FormatError, result.Error.Message);
        Assert.DoesNotContain("secret-content", result.Error.Message);
        Assert.Empty(result.OutputsJson);
        Assert.Equal(TaskOutcome.Succeeded, original.Outcome);
    }

    [Fact]
    public void TaskOutputs_rejects_invalid_UTF8_in_strings()
    {
        byte[] bytes = [(byte)'{', (byte)'"', (byte)'x', (byte)'"', (byte)':', (byte)'"', 0xff, (byte)'"', (byte)'}'];
        Assert.Equal(TaskOutputJson.FormatError, TaskOutputJson.Error(bytes));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{\"null\":null,\"array\":[false,0,{\"value\":\"safe\"}]}")]
    public void TaskOutputs_preserves_empty_legacy_and_nested_JSON(string json)
    {
        var result = OutputsGuard.Apply(Result(Encoding.UTF8.GetBytes(json)), new([]));
        Assert.Equal(TaskOutcome.Succeeded, result.Outcome);
        Assert.Equal(json, result.OutputsJson.ToStringUtf8());
    }

    [Fact]
    public void TaskOutputs_masks_before_journaling_values_names_base64_and_errors()
    {
        const string json = """{"token-secret":{"nested":["token-secret","dG9rZW4tc2VjcmV0"],"value":null}}""";
        var original = Result(Encoding.UTF8.GetBytes(json));
        original.Error = new() { Message = "fault token-secret" };
        var result = OutputsGuard.Apply(original, new(["token-secret"]));
        Assert.DoesNotContain("token-secret", result.OutputsJson.ToStringUtf8());
        Assert.DoesNotContain("dG9rZW4tc2VjcmV0", result.OutputsJson.ToStringUtf8());
        Assert.Equal("fault ***", result.Error.Message);
        Assert.Contains("token-secret", original.OutputsJson.ToStringUtf8());
    }

    [Fact]
    public void TaskOutputs_masking_collision_fails_without_unmasked_fallback()
    {
        var result = OutputsGuard.Apply(Result(Encoding.UTF8.GetBytes("""{"secret":1,"***":2}""")), new(["secret"]));
        Assert.Equal(TaskOutcome.Failed, result.Outcome);
        Assert.Empty(result.OutputsJson);
        Assert.Equal("Task outputs could not be safely masked.", result.Error.Message);
    }

    [Fact]
    public void TaskOutputs_masked_expansion_cannot_bypass_limit()
    {
        var result = OutputsGuard.Apply(Result(Encoding.UTF8.GetBytes("{\"x\":\"" + new string('s', 30000) + "\"}")), new(["s"]));
        // Contiguous redaction collapses to one marker, not one marker per byte.
        Assert.Equal(TaskOutcome.Succeeded, result.Outcome);
        Assert.Equal("""{"x":"***"}""", result.OutputsJson.ToStringUtf8());
        var expansion = OutputsGuard.Apply(Result(Encoding.UTF8.GetBytes("{\"x\":\"" + string.Concat(Enumerable.Repeat("s ", 22000)) + "\"}")), new(["s"]));
        Assert.Equal(TaskOutcome.Failed, expansion.Outcome);
        Assert.Equal(TaskOutputJson.SizeError, expansion.Error.Message);
    }
}
