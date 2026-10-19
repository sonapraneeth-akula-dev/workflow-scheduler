using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Wf.Contracts.Definition;
using Wf.Contracts.Outputs;

namespace Wf.Sdk;

public sealed class NodeOutputs
{
    public const int MaxBytes = TaskOutputJson.MaxBytes;
    private readonly string node;
    private readonly object sync = new();
    private ImmutableDictionary<string, JsonElement> values = ImmutableDictionary<string, JsonElement>.Empty;
    private ImmutableDictionary<string, JsonElement> changes = ImmutableDictionary<string, JsonElement>.Empty;

    public NodeOutputs(string node)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(node);
        this.node = node;
    }

    public ImmutableDictionary<string, JsonElement> Values
    {
        get { lock (sync) return values; }
    }

    public object? this[string key]
    {
        get => Read(key);
        set => Store(key, JsonSerializer.SerializeToElement(value, OutputJsonContext.Default.Object));
    }

    public T? Get<T>(string key, JsonTypeInfo<T>? typeInfo = null)
    {
        typeInfo ??= OutputJsonContext.Default.GetTypeInfo(typeof(T)) as JsonTypeInfo<T> ??
            throw new NotSupportedException($"Output '{node}/{key}' requires source-generated JSON type information for {typeof(T).Name}.");
        return Read(key).Deserialize(typeInfo);
    }

    public void Set<T>(string key, T value, JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        Store(key, JsonSerializer.SerializeToElement(value, typeInfo));
    }

    internal NodeOutputs ForAttempt() => new(node) { values = Values };

    internal void Commit(NodeOutputs attempt)
    {
        lock (sync)
        {
            var candidate = values.SetItems(attempt.changes);
            ValidateSize(candidate);
            values = candidate;
        }
    }

    private JsonElement Read(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (sync)
            return values.TryGetValue(key, out var value) ? value :
                throw new KeyNotFoundException($"Output '{node}/{key}' is not available.");
    }

    private void Store(string key, JsonElement value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (sync)
        {
            var candidate = values.SetItem(key, value.Clone());
            ValidateSize(candidate);
            values = candidate;
            changes = changes.SetItem(key, candidate[key]);
        }
    }

    private void ValidateSize(ImmutableDictionary<string, JsonElement> candidate)
    {
        var size = JsonSerializer.SerializeToUtf8Bytes(candidate,
            OutputJsonContext.Default.ImmutableDictionaryStringJsonElement).Length;
        if (size > MaxBytes)
            throw new InvalidOperationException($"Node '{node}' outputs are {size} bytes; limit is {MaxBytes} bytes. Publish an artifact instead.");
    }
}

[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(TimeSpan))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(ImmutableArray<string>))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(HttpResponseOutput))]
[JsonSerializable(typeof(SecretReference))]
[JsonSerializable(typeof(ImmutableDictionary<string, JsonElement>))]
internal partial class OutputJsonContext : JsonSerializerContext;
