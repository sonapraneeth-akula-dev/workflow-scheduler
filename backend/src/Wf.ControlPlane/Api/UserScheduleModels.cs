using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Primitives;
using Wf.Contracts.Schedules;

namespace Wf.ControlPlane.Api;

public sealed record UserScheduleRequest(string WorkflowId, string Name, string Timezone, string? Cron = null,
    TimeSpan? Interval = null, string? VersionPin = null, ScheduleOverlapPolicy Overlap = ScheduleOverlapPolicy.Skip,
    ScheduleCatchUpPolicy CatchUp = ScheduleCatchUpPolicy.Latest)
{
    public ImmutableDictionary<string, JsonElement> Variables { get; set; } = ImmutableDictionary<string, JsonElement>.Empty;
}

public sealed record UserScheduleResponse(Guid Id, Guid SeriesId, string WorkflowId, string Name, string Origin,
    string Timezone, string? Cron, TimeSpan? Interval, string? VersionPin, ScheduleOverlapPolicy Overlap,
    ScheduleCatchUpPolicy CatchUp, ImmutableDictionary<string, JsonElement> Variables, bool Paused,
    int RowVersion, DateTimeOffset? NextFireAt);

public sealed record UserSchedulePreview(ImmutableArray<DateTimeOffset> Fires);

public static class ScheduleEntityTag
{
    public static string Format(int version)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        return "\"" + version.ToString(CultureInfo.InvariantCulture) + "\"";
    }

    public static bool TryParse(StringValues values, out int version)
    {
        version = 0;
        if (values.Count != 1 || values[0] is not { Length: >= 3 } value || value[0] != '"' || value[^1] != '"')
            return false;
        var number = value.AsSpan(1, value.Length - 2);
        return int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out version) && version > 0 &&
            number.SequenceEqual(version.ToString(CultureInfo.InvariantCulture));
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(UserScheduleRequest))]
[JsonSerializable(typeof(UserScheduleResponse))]
[JsonSerializable(typeof(UserSchedulePreview))]
public partial class UserScheduleJson : JsonSerializerContext;
