using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wf.Contracts.Schedules;

[JsonConverter(typeof(ScheduleEnumConverter<ScheduleOverlapPolicy>))]
public enum ScheduleOverlapPolicy { Skip, Queue, Concurrent }
[JsonConverter(typeof(ScheduleEnumConverter<ScheduleCatchUpPolicy>))]
public enum ScheduleCatchUpPolicy { None, Latest }

public sealed class ScheduleEnumConverter<T>() : JsonStringEnumConverter<T>(allowIntegerValues: false)
    where T : struct, Enum;

public sealed record ScheduleFile(ImmutableArray<ScheduleInstance> Instances,
    ImmutableDictionary<string, ImmutableDictionary<string, JsonElement>> Defaults = null!,
    [property: JsonPropertyName("$schema")] string? Schema = null)
{
    public ImmutableDictionary<string, ImmutableDictionary<string, JsonElement>> Defaults { get; init; } =
        Defaults ?? ImmutableDictionary<string, ImmutableDictionary<string, JsonElement>>.Empty;
}

public sealed record ScheduleInstance(string Id, string Timezone, string? Cron = null, TimeSpan? Interval = null,
    string? VersionPin = null, ScheduleOverlapPolicy Overlap = ScheduleOverlapPolicy.Skip,
    ScheduleCatchUpPolicy CatchUp = ScheduleCatchUpPolicy.Latest,
    ImmutableDictionary<string, JsonElement> Variables = null!)
{
    public ImmutableDictionary<string, JsonElement> Variables { get; init; } =
        Variables ?? ImmutableDictionary<string, JsonElement>.Empty;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true, WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true,
    AllowDuplicateProperties = false)]
[JsonSerializable(typeof(ScheduleFile))]
public partial class ScheduleFileJsonContext : JsonSerializerContext;
