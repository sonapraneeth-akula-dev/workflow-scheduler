using System.Net;
using System.Net.Http.Json;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;
using Wf.Agent.Logs;
using Wf.Agent.Secrets;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Registration;

namespace Wf.Agent.Tests;

public sealed class LogSpoolUploaderTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly Uri Engine = new("http://localhost:7000");

    [Theory]
    [InlineData("https://elsewhere.example/upload")]
    [InlineData("http://elsewhere.example/upload")]
    [InlineData("https://name:credential@localhost/upload")]
    [InlineData("http://localhost:7000/upload#fragment")]
    [InlineData("")]
    [InlineData("file:///C:/outside")]
    [InlineData("//elsewhere.example/upload")]
    public async Task Untrusted_grant_locations_are_rejected_before_any_HTTP_or_spool_deletion(string url)
    {
        using var files = new LogFiles();
        using var handler = new ProbeHandler();
        using var http = new HttpClient(handler);
        await using var spool = new LogSpooler(files.Root, LogSpoolTests.Attempt(), new SecretMasker([]));
        await spool.CompleteAsync();
        var grant = Grant(TimeProvider.System);
        grant.UploadUri = url;
        var uploader = new LogChunkUploader(http, TimeProvider.System, NullLogger<LogChunkUploader>.Instance);
        await Assert.ThrowsAsync<InvalidDataException>(() => uploader.UploadAsync(spool, grant, Engine, Token));
        Assert.Equal(0, handler.Count);
        Assert.False(spool.IsUploaded);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("expired")]
    [InlineData("malformed")]
    [InlineData("kind")]
    [InlineData("limit")]
    public async Task Missing_expired_or_malformed_capabilities_fail_closed_without_an_HTTP_request(string invalid)
    {
        using var files = new LogFiles();
        using var handler = new ProbeHandler();
        using var http = new HttpClient(handler);
        await using var spool = new LogSpooler(files.Root, LogSpoolTests.Attempt(), new SecretMasker([]));
        await spool.CompleteAsync();
        var grant = Grant(TimeProvider.System);
        switch (invalid)
        {
            case "missing": grant.ExpiresAt = null; break;
            case "expired": grant.ExpiresAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(-1)); break;
            case "malformed": grant.ExpiresAt = new Timestamp { Seconds = long.MaxValue }; break;
            case "kind": grant.Kind = "package"; break;
            case "limit": grant.MaxBytes = 0; break;
        }
        var uploader = new LogChunkUploader(http, TimeProvider.System, NullLogger<LogChunkUploader>.Instance);
        await Assert.ThrowsAsync<InvalidDataException>(() => uploader.UploadAsync(spool, grant, Engine, Token));
        Assert.Equal(0, handler.Count);
        Assert.False(spool.IsUploaded);
    }

    [Fact]
    public async Task Transient_backoff_uses_fake_clock_exact_one_then_two_second_boundaries()
    {
        using var files = new LogFiles();
        var clock = new RecordingClock();
        using var handler = new ProbeHandler(transientResponses: 2);
        using var http = new HttpClient(handler);
        await using var spool = new LogSpooler(files.Root, LogSpoolTests.Attempt(), new SecretMasker([]), timeProvider: clock);
        await spool.WriteAsync("stdout", "retry", cancellationToken: Token);
        await spool.CompleteAsync();
        var upload = new LogChunkUploader(http, clock, NullLogger<LogChunkUploader>.Instance)
            .UploadAsync(spool, Grant(clock), Engine, Token);
        Assert.Equal(1, handler.Count);
        clock.Advance(TimeSpan.FromMilliseconds(999));
        Assert.Equal(1, handler.Count);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await LogSpoolTests.UntilAsync(() => Task.FromResult(clock.Backoffs == 2));
        clock.Advance(TimeSpan.FromMilliseconds(1999));
        Assert.Equal(2, handler.Count);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await upload.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.True(spool.IsUploaded);
        Assert.Equal(4, handler.Count);
    }

    private static UploadGrant Grant(TimeProvider clock) => new()
    {
        Kind = "logs", MaxBytes = 16 * 1024 * 1024, UploadUri = "/api/v1/log-upload-grants/test",
        ExpiresAt = Timestamp.FromDateTimeOffset(clock.GetUtcNow().AddHours(1))
    };

    private sealed class ProbeHandler(int transientResponses = -1) : HttpMessageHandler
    {
        private int count;
        public int Count => Volatile.Read(ref count);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var current = Interlocked.Increment(ref count);
            if (transientResponses < 0) throw new InvalidOperationException("Untrusted log grant caused an HTTP request.");
            return Task.FromResult(current <= transientResponses
                ? new HttpResponseMessage(current == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.TooManyRequests)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new UploadGrantResponse(true), RegistrationJsonContext.Default.UploadGrantResponse)
                });
        }
    }

    private sealed class RecordingClock : TimeProvider
    {
        private readonly FootprintClock inner = new();
        private int backoffs;
        public int Backoffs => Volatile.Read(ref backoffs);
        public override long TimestampFrequency => inner.TimestampFrequency;
        public override long GetTimestamp() => inner.GetTimestamp();
        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();
        public void Advance(TimeSpan elapsed) => inner.Advance(elapsed);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = inner.CreateTimer(callback, state, dueTime, period);
            if (dueTime > TimeSpan.Zero && dueTime <= TimeSpan.FromSeconds(2)) Interlocked.Increment(ref backoffs);
            return timer;
        }
    }
}
