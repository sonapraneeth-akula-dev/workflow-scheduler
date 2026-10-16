using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Logs;
using Wf.ControlPlane.Storage;

namespace Wf.ControlPlane.Tests.Storage;

public sealed class LogSpoolGrantTests
{
    [Fact]
    public void Attempt_capability_is_bound_to_full_identity_and_a_finite_recovery_horizon()
    {
        var time = new GrantClock();
        using var caps = new LogUploadCapabilities(RandomNumberGenerator.GetBytes(32), time);
        var attempt = Attempt();
        var deadline = time.GetUtcNow().AddHours(1);
        var cap = caps.Create(attempt, deadline);
        Assert.Equal("logs", cap.Kind);
        Assert.Equal(deadline.AddDays(1), cap.ExpiresAt.ToDateTimeOffset());
        Assert.Equal(LogIndex.PrefixFor(attempt), caps.Read(cap.UploadUri.Split('/')[^1]));
        var retry = attempt.Clone();
        retry.Attempt++;
        Assert.NotEqual(LogIndex.PrefixFor(attempt), caps.Read(caps.Create(retry, deadline).UploadUri.Split('/')[^1]));
        time.Now = deadline.AddDays(1);
        Assert.Throws<UnauthorizedAccessException>(() => caps.Read(cap.UploadUri.Split('/')[^1]));
    }

    [Fact]
    public void Named_log_object_grant_round_trips_and_expires_at_the_exact_short_lived_boundary()
    {
        var time = new GrantClock();
        var key = RandomNumberGenerator.GetBytes(32);
        using var grants = new BlobUploadGrants(key, timeProvider: time);
        var content = new BlobContent(BlobKind.Logs, new string('a', 64), 20, LogIndex.PrefixFor(Attempt()) + "000000.log.gz");
        var grant = grants.Create(content, content.SizeBytes, TimeSpan.FromMinutes(10));
        var read = grants.Read(grant);
        Assert.Equal(content.Key, read.Key);
        Assert.Equal(content.Sha256, read.Sha256);
        Assert.Equal(content.SizeBytes, read.SizeBytes);
        time.Now = time.Now.AddMinutes(10).AddTicks(-1);
        Assert.Equal(content.Key, grants.Read(grant).Key);
        time.Now = time.Now.AddTicks(1);
        Assert.Throws<UnauthorizedAccessException>(() => grants.Read(grant));
    }

    [Theory]
    [InlineData("../index.json")]
    [InlineData("/index.json")]
    [InlineData("other/000000.log.gz")]
    [InlineData("000000.log.gz\\..\\index.json")]
    [InlineData("000000.log.gz%2f..")]
    [InlineData("index.json?grant=credential")]
    public void Named_log_objects_reject_noncanonical_or_cross_prefix_names(string name) =>
        Assert.Throws<ArgumentException>(() =>
            new BlobContent(BlobKind.Logs, new string('a', 64), 1, LogIndex.PrefixFor(Attempt()) + name));

    [Fact]
    public void Non_log_content_cannot_choose_a_named_log_destination() =>
        Assert.Throws<ArgumentException>(() =>
            new BlobContent(BlobKind.Packages, new string('a', 64), 1, LogIndex.PrefixFor(Attempt()) + "index.json"));

    [Fact]
    public void Malformed_signed_log_payload_fails_as_unauthorized_not_a_null_reference_or_path_fallback()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        using var grants = new BlobUploadGrants(key);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Kind = BlobKind.Logs, Sha256 = new string('a', 64), SizeBytes = 1, Key = (string?)null,
            MaxBytes = 1, ExpiresUtcTicks = DateTimeOffset.UtcNow.AddMinutes(1).UtcTicks, Nonce = new string('a', 32)
        });
        var token = WebEncoders.Base64UrlEncode(payload) + "." + WebEncoders.Base64UrlEncode(HMACSHA256.HashData(key, payload));
        Assert.Throws<UnauthorizedAccessException>(() => grants.Read(token));
    }

    [Fact]
    public void Disposed_capability_signer_cannot_mint_zero_key_grants()
    {
        var caps = new LogUploadCapabilities(RandomNumberGenerator.GetBytes(32), TimeProvider.System);
        caps.Dispose();
        Assert.Throws<ObjectDisposedException>(() => caps.Create(Attempt(), DateTimeOffset.UtcNow.AddHours(1)));
        Assert.Throws<ObjectDisposedException>(() => caps.Read("invalid"));
    }

    private static AttemptRef Attempt() => new() { RunId = Guid.NewGuid().ToString(), NodeKey = "node", TaskKey = "task", Attempt = 1 };
    private sealed class GrantClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
