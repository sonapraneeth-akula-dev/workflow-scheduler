using System.Text.Json;
using System.Text.Json.Serialization;
using Wf.Contracts.Runs;

namespace Wf.Contracts.Reports;

public sealed record ReportIndexResponse
{
    public required Guid RunId { get; init; }
    public required ReportIndexItem[] Items { get; init; }
}

public sealed record ReportIndexItem
{
    public required Guid Id { get; init; }
    public required Guid RunId { get; init; }
    public required string SourceKey { get; init; }
    public required int Attempt { get; init; }
    public required string Kind { get; init; }
    public required string Title { get; init; }
    public required string[] Tags { get; init; }
    public required long SizeBytes { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    [JsonRequired]
    public SignOffState SignOff { get; init; } = SignOffState.Empty;
}

[JsonSerializable(typeof(ReportIndexResponse))]
[JsonSerializable(typeof(ReportIndexItem))]
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
public partial class ReportIndexJsonContext : JsonSerializerContext;
