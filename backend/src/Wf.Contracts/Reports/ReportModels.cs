using System.Text;
using System.Text.Json.Serialization;

namespace Wf.Contracts.Reports;

public sealed record ReportUploadMetadata(string Kind, string Title, string[] Tags, string Sha256, long SizeBytes)
{
    public const string HeaderName = "X-Wf-Report-Metadata";
    public const string EndpointPath = "/api/v1/report-publishes";

    public void Validate()
    {
        ValidateDescription(Kind, Title, Tags);
        if (Sha256 is not { Length: 64 } || Sha256.Any(character => !char.IsAsciiHexDigitLower(character)))
            throw new ArgumentException("Report SHA-256 must be 64 lowercase hexadecimal characters.", nameof(Sha256));
        ArgumentOutOfRangeException.ThrowIfNegative(SizeBytes);
    }

    public static void ValidateDescription(string kind, string title, IReadOnlyList<string> tags)
    {
        ValidateText(kind, 128, nameof(Kind));
        ValidateText(title, 512, nameof(Title));
        if (tags is null || tags.Count > 32)
            throw new ArgumentException("Reports support at most 32 tags.", nameof(Tags));
        foreach (var tag in tags)
            ValidateText(tag, 128, nameof(Tags));
    }

    private static void ValidateText(string value, int maximumBytes, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        if (Encoding.UTF8.GetByteCount(value) > maximumBytes)
            throw new ArgumentException($"Report {name} exceeds {maximumBytes} UTF-8 bytes.", name);
    }
}

public sealed record ReportReceipt
{
    public required Guid RunId { get; init; }
    public required Guid ReportId { get; init; }
}

public sealed record ReportAdded
{
    public required Guid RunId { get; init; }
    public required Guid ReportId { get; init; }
}

[JsonSerializable(typeof(ReportUploadMetadata))]
[JsonSerializable(typeof(ReportReceipt))]
[JsonSerializable(typeof(ReportAdded))]
public partial class ReportJsonContext : JsonSerializerContext;
