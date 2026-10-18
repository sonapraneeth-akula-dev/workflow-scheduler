using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace Wf.Contracts.Tests;

public sealed class CapacityProfileTests
{
    [Fact]
    public void Profile_key_is_canonical_independent_of_settings_comparer_and_insertion_order()
    {
        var reverse = ImmutableSortedDictionary.Create<string, string>(Comparer<string>.Create((a, b) =>
            StringComparer.Ordinal.Compare(b, a))).Add("threads", "8").Add("connections", "4");
        var first = new CapacityProfile("http", "json", "small", reverse);
        var second = new CapacityProfile("http", "json", "small",
            ImmutableSortedDictionary<string, string>.Empty.Add("connections", "4").Add("threads", "8"));
        Assert.Equal(first.ProfileKey, second.ProfileKey);
        Assert.Equal("""{"transport":"http","codec":"json","payloadSizeClass":"small","loadSettings":{"connections":"4","threads":"8"}}""",
            first.ProfileKey);
    }

    [Fact]
    public void Every_dimension_participates_in_profile_identity_without_delimiter_collisions()
    {
        var profile = new CapacityProfile("http", "json", "small");
        Assert.NotEqual(profile.ProfileKey, new CapacityProfile("grpc", "json", "small").ProfileKey);
        Assert.NotEqual(profile.ProfileKey, new CapacityProfile("http", "protobuf", "small").ProfileKey);
        Assert.NotEqual(profile.ProfileKey, new CapacityProfile("http", "json", "large").ProfileKey);
        Assert.NotEqual(profile.ProfileKey, new CapacityProfile("http", "json", "small",
            ImmutableSortedDictionary<string, string>.Empty.Add("threads", "8")).ProfileKey);
        Assert.NotEqual(new CapacityProfile("a|b", "c", "d").ProfileKey,
            new CapacityProfile("a", "b|c", "d").ProfileKey);
        Assert.NotEqual(new CapacityProfile("HTTP", "json", "small").ProfileKey, profile.ProfileKey);
    }

    [Fact]
    public void Profile_and_capacity_round_trip_through_source_generated_json()
    {
        var profile = new CapacityProfile("http", "json", "small",
            ImmutableSortedDictionary<string, string>.Empty.Add("path", "quotes\"\\\u2603"));
        var parsed = JsonSerializer.Deserialize(profile.ProfileKey, CapacityProfileJsonContext.Default.CapacityProfile)!;
        Assert.Equal(profile.ProfileKey, parsed.ProfileKey);
        var capacity = new AgentCapacity(Guid.NewGuid(), profile, 123, CapacitySource.Calibrated, DateTimeOffset.UnixEpoch);
        var json = JsonSerializer.Serialize(capacity, CapacityProfileJsonContext.Default.AgentCapacity);
        var restored = JsonSerializer.Deserialize(json, CapacityProfileJsonContext.Default.AgentCapacity)!;
        Assert.Equal(capacity.AgentId, restored.AgentId);
        Assert.Equal(profile.ProfileKey, restored.Profile.ProfileKey);
        Assert.Equal(123, restored.CapacityRps);
        Assert.Equal(CapacitySource.Calibrated, restored.Source);
        Assert.Equal(DateTimeOffset.UnixEpoch, restored.CalibratedAt);
    }

    [Theory]
    [InlineData("", "json", "small")]
    [InlineData("http", " ", "small")]
    [InlineData("http", "json", "\t")]
    [InlineData(" http", "json", "small")]
    [InlineData("http", "json", "small\n")]
    public void Invalid_profile_identifiers_are_rejected(string transport, string codec, string payload)
    {
        Assert.ThrowsAny<ArgumentException>(() => new CapacityProfile(transport, codec, payload));
    }

    [Fact]
    public void Profile_key_is_bounded_for_the_composite_database_index()
    {
        var prefix = new CapacityProfile("http", "json", "small",
            ImmutableSortedDictionary<string, string>.Empty.Add("x", "")).ProfileKey;
        var remaining = 2000 - Encoding.UTF8.GetByteCount(prefix);
        var settings = ImmutableSortedDictionary<string, string>.Empty.Add("x", new string('a', remaining));
        Assert.Equal(2000, Encoding.UTF8.GetByteCount(new CapacityProfile("http", "json", "small", settings).ProfileKey));
        Assert.Throws<ArgumentException>(() => new CapacityProfile("http", "json", "small",
            settings.SetItem("x", new string('a', remaining + 1))));
        Assert.Throws<ArgumentException>(() => new CapacityProfile("http", "json", "small",
            settings.SetItem("x", new string('\u2603', remaining))));
        Assert.Throws<ArgumentException>(() => new CapacityProfile(new string('a', 101), "json", "small"));
        Assert.ThrowsAny<ArgumentException>(() => new CapacityProfile("http", "json", "small",
            ImmutableSortedDictionary<string, string>.Empty.Add(" ", "x")));
        Assert.Throws<ArgumentNullException>(() => new CapacityProfile("http", "json", "small",
            ImmutableSortedDictionary<string, string>.Empty.Add("x", null!)));
    }

    [Theory]
    [InlineData("""{"transport":"http","codec":"json","payloadSizeClass":"small","extra":1}""")]
    [InlineData("""{"transport":"http","transport":"grpc","codec":"json","payloadSizeClass":"small"}""")]
    [InlineData("""{"transport":"http","codec":"json"}""")]
    [InlineData("""{"transport":null,"codec":"json","payloadSizeClass":"small"}""")]
    public void Invalid_json_is_not_silently_accepted(string json)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json, CapacityProfileJsonContext.Default.CapacityProfile));
    }

    [Fact]
    public void Capacity_requires_positive_rate_valid_identity_source_and_consistent_utc_timestamp()
    {
        var id = Guid.NewGuid();
        var profile = new CapacityProfile("http", "json", "small");
        Assert.Null(new AgentCapacity(id, profile, 1, CapacitySource.Manual).CalibratedAt);
        Assert.Throws<ArgumentException>(() => new AgentCapacity(Guid.Empty, profile, 1, CapacitySource.Manual));
        Assert.Throws<ArgumentNullException>(() => new AgentCapacity(id, null!, 1, CapacitySource.Manual));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentCapacity(id, profile, 0, CapacitySource.Manual));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentCapacity(id, profile, -1, CapacitySource.Manual));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentCapacity(id, profile, 1, (CapacitySource)2));
        Assert.Throws<ArgumentException>(() => new AgentCapacity(id, profile, 1, CapacitySource.Calibrated));
        Assert.Throws<ArgumentException>(() => new AgentCapacity(id, profile, 1, CapacitySource.Manual, DateTimeOffset.UnixEpoch));
        Assert.Throws<ArgumentException>(() => new AgentCapacity(id, profile, 1, CapacitySource.Calibrated,
            DateTimeOffset.UnixEpoch.ToOffset(TimeSpan.FromHours(1))));
    }
}
