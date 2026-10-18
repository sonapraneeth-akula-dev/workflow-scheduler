using System.Text.Json.Serialization;
using MessagePack;

namespace Wf.ControlPlane.Live;

[MessagePackObject]
public sealed record LiveLogUpdate(
    [property: Key("id")] string Id,
    [property: Key("records")] LiveLogLine[] Records,
    [property: Key("gap")] bool Gap);

[MessagePackObject]
public sealed record LiveLogLine(
    [property: Key("line")] long Line,
    [property: Key("at")] string At,
    [property: Key("stream")] string Stream,
    [property: Key("level")] string Level,
    [property: Key("text")] string Text);

public sealed record LiveLogPointer(string Id, string Key, string Sha256, long SizeBytes);
public sealed record LiveLogInterest(Guid Owner, string Id, bool On);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false)]
[JsonSerializable(typeof(LiveLogPointer))]
[JsonSerializable(typeof(LiveLogInterest))]
public partial class LiveLogJson : JsonSerializerContext;
