using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Protobuf;
using Wf.Contracts.Agent.V1;

namespace Wf.Contracts.Outputs;

public static class TaskOutputJson
{
    public const int MaxBytes = 64 * 1024;
    public const string SizeError = "Task outputs exceed the 65536-byte UTF-8 JSON limit. Publish an artifact instead.";
    public const string FormatError = "Task outputs must be a valid UTF-8 JSON object with unique property names.";
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    public static string? Error(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length > MaxBytes) return SizeError;
        if (bytes.IsEmpty) return null; // Older producers omit outputs.
        try
        {
            _ = StrictUtf8.GetCharCount(bytes.Span);
            using var document = JsonDocument.Parse(bytes);
            return document.RootElement.ValueKind == JsonValueKind.Object && Valid(document.RootElement) ? null : FormatError;
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or InvalidOperationException)
        { return FormatError; }
    }

    private static bool Valid(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
                if (!names.Add(property.Name) || !Valid(property.Value)) return false;
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
                if (!Valid(item)) return false;
        }
        else if (value.ValueKind == JsonValueKind.String) _ = value.GetString();
        return true;
    }

    public static TaskCompleted Reject(TaskCompleted result, string message)
    {
        var rejected = result.Clone();
        rejected.OutputsJson = ByteString.Empty;
        rejected.Outcome = TaskOutcome.Failed;
        rejected.Error = new TaskError { Category = "ValidationError", Message = message, NonRetryable = true };
        return rejected;
    }
}

public sealed record TaskOutputsResponse(Guid RunId, string TaskKey, int Attempt, JsonElement? Outputs);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(TaskOutputsResponse))]
public sealed partial class TaskOutputsJsonContext : JsonSerializerContext;
