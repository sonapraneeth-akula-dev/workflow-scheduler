using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wf.Contracts;

public sealed record CapacityProfile
{
    public string Transport { get; }
    public string Codec { get; }
    public string PayloadSizeClass { get; }
    public ImmutableSortedDictionary<string, string> LoadSettings { get; }

    [JsonIgnore]
    public string ProfileKey => JsonSerializer.Serialize(this, CapacityProfileJsonContext.Default.CapacityProfile);

    public CapacityProfile(string transport, string codec, string payloadSizeClass,
        ImmutableSortedDictionary<string, string>? loadSettings = null)
    {
        ValidateName(transport, nameof(transport));
        ValidateName(codec, nameof(codec));
        ValidateName(payloadSizeClass, nameof(payloadSizeClass));
        var settings = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        if (loadSettings is not null)
            foreach (var (key, value) in loadSettings)
            {
                ValidateName(key, nameof(loadSettings));
                ArgumentNullException.ThrowIfNull(value);
                settings.Add(key, value);
            }
        Transport = transport;
        Codec = codec;
        PayloadSizeClass = payloadSizeClass;
        LoadSettings = settings.ToImmutable();
        if (Encoding.UTF8.GetByteCount(ProfileKey) > 2000)
            throw new ArgumentException("A capacity profile key must fit 2000 UTF-8 bytes.", nameof(loadSettings));
    }

    private static void ValidateName(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        if (value.Length > 100 || value != value.Trim() || value.Any(char.IsControl))
            throw new ArgumentException("Profile identifiers must be 1-100 characters without surrounding whitespace or controls.", name);
    }
}

public enum CapacitySource { Manual, Calibrated }

public sealed record AgentCapacity
{
    public Guid AgentId { get; }
    public CapacityProfile Profile { get; }
    public int CapacityRps { get; }
    public CapacitySource Source { get; }
    public DateTimeOffset? CalibratedAt { get; }

    public AgentCapacity(Guid agentId, CapacityProfile profile, int capacityRps, CapacitySource source,
        DateTimeOffset? calibratedAt = null)
    {
        if (agentId == Guid.Empty) throw new ArgumentException("Agent identity cannot be empty.", nameof(agentId));
        ArgumentNullException.ThrowIfNull(profile);
        if (capacityRps <= 0) throw new ArgumentOutOfRangeException(nameof(capacityRps));
        if (!Enum.IsDefined(source)) throw new ArgumentOutOfRangeException(nameof(source));
        if ((source == CapacitySource.Calibrated) != calibratedAt.HasValue)
            throw new ArgumentException("Only calibrated capacity requires a calibration timestamp.", nameof(calibratedAt));
        if (calibratedAt is { Offset: var offset } && offset != TimeSpan.Zero)
            throw new ArgumentException("Calibration timestamps must use UTC.", nameof(calibratedAt));
        AgentId = agentId;
        Profile = profile;
        CapacityRps = capacityRps;
        Source = source;
        CalibratedAt = calibratedAt;
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true,
    AllowDuplicateProperties = false)]
[JsonSerializable(typeof(CapacityProfile))]
[JsonSerializable(typeof(AgentCapacity))]
public partial class CapacityProfileJsonContext : JsonSerializerContext;
