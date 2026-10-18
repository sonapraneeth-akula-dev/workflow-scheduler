using System.Text;
using System.Text.Json;
using Wf.Contracts;
using Wf.Contracts.Definition;

namespace Wf.Sdk.Tests;

public sealed class CheckpointSecretLeakTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Value = "checkpoint-canary-\u00e9-\"\0\n";

    [Theory]
    [InlineData("string")]
    [InlineData("base64")]
    [InlineData("key")]
    [InlineData("nested")]
    [InlineData("escaped")]
    [InlineData("nested-json")]
    public async Task Resolved_secret_content_fails_before_publish_sequence_or_state_change(string shape)
    {
        var calls = 0;
        var checkpoint = new CheckpointStore((bytes, sequence, at, token) =>
        {
            Assert.Equal(1, sequence);
            calls++;
            return Task.CompletedTask;
        });
        var payload = shape switch
        {
            "string" => JsonSerializer.SerializeToElement("prefix " + Value),
            "base64" => JsonSerializer.SerializeToElement(Convert.ToBase64String(Encoding.UTF8.GetBytes(Value))),
            "key" => JsonSerializer.SerializeToElement(new Dictionary<string, string> { [Value] = "safe" }),
            "nested" => JsonSerializer.SerializeToElement(new { items = new[] { new { data = Value } } }),
            "escaped" => JsonSerializer.Deserialize<JsonElement>(
                JsonSerializer.Serialize(Value).Replace("\u00e9", "\\u00e9", StringComparison.Ordinal)),
            _ => JsonSerializer.SerializeToElement(JsonSerializer.Serialize(new { data = Value }))
        };
        var workflow = Workflow.Define("tests/checkpoint/secrets", "1", w => w.Type("tests")
            .Node("work", n => n.Task("save", async context =>
            {
                Assert.Equal(Value, context.Secret("secret://local/credential"));
                var error = await Assert.ThrowsAsync<TaskFailureException>(() =>
                    context.Checkpoint.SaveAsync(payload, context.CancellationToken));
                Assert.False(error.Retryable);
                Assert.Equal(FailureCategory.ValidationError, error.Category);
                Assert.Equal("Checkpoint state contains a resolved secret value.", error.Message);
                Assert.DoesNotContain(Value, error.ToString());
                Assert.Equal(JsonValueKind.Undefined, checkpoint.TryLoad<JsonElement>().ValueKind);
                Assert.Equal(0, calls);
                await context.Checkpoint.SaveAsync("safe-state", context.CancellationToken);
            })));
        await new TaskRegistry(workflow).ExecuteAsync("work/save", new(Token, checkpoint: checkpoint,
            secrets: new Dictionary<string, string> { ["secret://local/credential"] = Value }));
        Assert.Equal(1, calls);
        Assert.Equal("safe-state", checkpoint.TryLoad<string>());
    }

    [Theory]
    [InlineData("12345", "12345")]
    [InlineData("true", "true")]
    [InlineData("null", "null")]
    public async Task Secret_bearing_non_string_JSON_scalars_are_also_rejected(string secret, string json)
    {
        var checkpoint = new CheckpointStore((_, _, _, _) => throw new InvalidOperationException("Must not publish"));
        _ = new TaskContext(secrets: new Dictionary<string, string> { ["secret://local/scalar"] = secret },
            checkpoint: checkpoint);
        await Assert.ThrowsAsync<TaskFailureException>(() =>
            checkpoint.SaveAsync(JsonSerializer.Deserialize<JsonElement>(json), Token));
    }

    [Fact]
    public async Task All_resolved_values_are_guarded_even_when_a_store_is_shared_between_contexts()
    {
        var calls = 0;
        var checkpoint = new CheckpointStore((_, _, _, _) => { calls++; return Task.CompletedTask; });
        _ = new TaskContext(checkpoint: checkpoint,
            secrets: new Dictionary<string, string> { ["secret://local/one"] = "first-canary" });
        _ = new TaskContext(checkpoint: checkpoint,
            secrets: new Dictionary<string, string> { ["secret://local/two"] = "second-canary" });
        await Assert.ThrowsAsync<TaskFailureException>(() => checkpoint.SaveAsync("first-canary", Token));
        await Assert.ThrowsAsync<TaskFailureException>(() => checkpoint.SaveAsync("second-canary", Token));
        await checkpoint.SaveAsync("secret://local/one", Token);
        Assert.Equal(1, calls);
    }
}
