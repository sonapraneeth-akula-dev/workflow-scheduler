using System.Text.Json;

namespace Wf.Contracts;

public sealed record ReportLimits
{
    public static ReportLimits Default { get; } = new();
    public int MaxCount { get; }
    public long MaxBytes { get; }

    public ReportLimits(int maxCount = 10, long maxBytes = 10_000_000)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        MaxCount = maxCount;
        MaxBytes = maxBytes;
    }

    public static ReportLimits FromTypeLimits(JsonElement? typeLimits)
    {
        if (typeLimits is null || typeLimits.Value.ValueKind == JsonValueKind.Null)
            return Default;
        if (typeLimits.Value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Type limits must be a JSON object.", nameof(typeLimits));
        JsonElement? reports = null;
        foreach (var property in typeLimits.Value.EnumerateObject())
        {
            if (property.Name != "reports")
                continue;
            if (reports is not null)
                throw new ArgumentException("Type limits contain duplicate reports settings.", nameof(typeLimits));
            reports = property.Value;
        }
        if (reports is null)
            return Default;
        if (reports.Value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Type report limits must be a JSON object.", nameof(typeLimits));
        var maxCount = Default.MaxCount;
        var maxBytes = Default.MaxBytes;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in reports.Value.EnumerateObject())
        {
            if (!seen.Add(property.Name))
                throw new ArgumentException($"Duplicate report limit '{property.Name}'.", nameof(typeLimits));
            switch (property.Name)
            {
                case "maxCount" when property.Value.ValueKind == JsonValueKind.Number &&
                                     property.Value.TryGetInt32(out var count) && count > 0:
                    maxCount = count;
                    break;
                case "maxBytes" when property.Value.ValueKind == JsonValueKind.Number &&
                                     property.Value.TryGetInt64(out var bytes) && bytes > 0:
                    maxBytes = bytes;
                    break;
                default:
                    throw new ArgumentException($"Invalid report limit '{property.Name}'; use positive integer maxCount/maxBytes.",
                        nameof(typeLimits));
            }
        }
        return new(maxCount, maxBytes);
    }
}

public sealed class ReportLimitExceededException(string message)
    : TaskFailureException(message, retryable: false);
