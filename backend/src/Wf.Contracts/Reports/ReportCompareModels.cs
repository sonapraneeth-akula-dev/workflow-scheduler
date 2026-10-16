using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wf.Contracts.Reports;

public sealed record ReportCompareResponse
{
    public required Guid SeriesId { get; init; }
    public required ReportIndexItem Left { get; init; }
    public required ReportIndexItem Right { get; init; }
}

[JsonSerializable(typeof(ReportCompareResponse))]
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
public partial class ReportCompareJsonContext : JsonSerializerContext;
