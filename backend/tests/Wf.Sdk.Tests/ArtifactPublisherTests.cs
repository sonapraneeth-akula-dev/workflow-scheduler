using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Google.Protobuf.WellKnownTypes;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Artifacts;

namespace Wf.Sdk.Tests;

public sealed class ArtifactPublisherTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Seekable_and_nonseekable_streams_publish_hash_and_exact_bytes_without_owning_input(bool nonseekable)
    {
        var bytes = new byte[300_123];
        RandomNumberGenerator.Fill(bytes);
        var handler = new Handler(bytes);
        using var http = new HttpClient(handler);
        using var input = new Input(bytes, nonseekable);
        var publisher = Publisher(http, handler.RunId);
        Uri? result = null;
        var workflow = Workflow.Define("artifacts", "1", w => w.Type("test")
            .Node("n", n => n.Task("t", async ctx =>
                result = await ctx.Artifacts.PublishAsync("raw.bin", input, "application/octet-stream", Token))));
        await new TaskRegistry(workflow).ExecuteAsync("n/t", new(Token, artifacts: publisher));
        Assert.Equal(["POST", "PUT"], handler.Methods);
        Assert.Equal(new Uri("https://engine.test/api/v1/artifacts/" + handler.Id + "/content"), result);
        Assert.True(input.CanRead);
        Assert.InRange(input.LargestRead, 1, 128 * 1024);
        Assert.Empty(Directory.GetFiles(Directory.GetCurrentDirectory(), ".wf-artifact-*"));
    }

    [Theory]
    [InlineData("../secret", "application/octet-stream")]
    [InlineData("x\\secret", "application/octet-stream")]
    [InlineData("x\r\n", "application/octet-stream")]
    [InlineData("file", "text/plain\r\nX: y")]
    [InlineData("file", "text/plain; charset=utf-8")]
    public async Task Invalid_metadata_never_reads_input_or_calls_server(string name, string mime)
    {
        using var http = new HttpClient(new Handler([]));
        var publisher = Publisher(http, Guid.CreateVersion7());
        var workflow = Workflow.Define("artifacts", "1", w => w.Type("test").Node("n", n => n.Task("t", async ctx =>
            await ctx.Artifacts.PublishAsync(name, new Unreadable(), mime, Token))));
        await Assert.ThrowsAsync<ArgumentException>(() => new TaskRegistry(workflow)
            .ExecuteAsync("n/t", new(Token, artifacts: publisher)));
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("oversize")]
    [InlineData("source")]
    public async Task Invalid_lifetime_size_or_source_fail_before_network(string scenario)
    {
        var handler = new Handler([]);
        using var http = new HttpClient(handler);
        var publisher = Publisher(http, handler.RunId);
        using var cancel = new CancellationTokenSource();
        if (scenario == "cancel") cancel.Cancel();
        using var stream = scenario == "oversize" ? new Huge() : new MemoryStream();
        var workflow = Workflow.Define("artifacts", "1", w => w.Type("test").Node("n", n => n.Task("t", async ctx =>
            await ctx.Artifacts.PublishAsync("raw.bin", stream, "application/octet-stream", cancel.Token))));
        Func<Task> execute = scenario == "source"
            ? async () => await publisher.PublishAsync("raw.bin", stream, "application/octet-stream", Token)
            : () => new TaskRegistry(workflow).ExecuteAsync("n/t", new(Token, artifacts: publisher));
        if (scenario == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(execute);
        if (scenario == "oversize")
            Assert.False((await Assert.ThrowsAsync<ArtifactLimitExceededException>(execute)).Retryable);
        if (scenario == "source") await Assert.ThrowsAsync<InvalidOperationException>(execute);
        Assert.Empty(handler.Methods);
    }

    [Theory]
    [InlineData("bad-grant")]
    [InlineData("bad-receipt")]
    [InlineData("server-error")]
    [InlineData("oversized-grant")]
    [InlineData("oversized-receipt")]
    public async Task Failed_publication_releases_reservation_and_preserves_caller_stream(string scenario)
    {
        var handler = new Handler([1, 2, 3], scenario);
        using var http = new HttpClient(handler);
        using var input = new Input([1, 2, 3], true);
        var publisher = Publisher(http, handler.RunId);
        var workflow = Workflow.Define("artifacts", "1", w => w.Type("test").Node("n", n => n.Task("t", async ctx =>
            await ctx.Artifacts.PublishAsync("raw.bin", input, "application/octet-stream", Token))));
        await Assert.ThrowsAnyAsync<Exception>(() => new TaskRegistry(workflow).ExecuteAsync("n/t", new(Token, artifacts: publisher)));
        Assert.Equal("DELETE", handler.Methods[^1]);
        Assert.True(input.CanRead);
        Assert.Empty(Directory.GetFiles(Directory.GetCurrentDirectory(), ".wf-artifact-*"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_or_cancelled_nonseekable_hashing_discards_staging_before_requesting_a_grant(bool cancel)
    {
        var handler = new Handler([]);
        using var http = new HttpClient(handler);
        using var input = new BrokenInput(cancel);
        var publisher = Publisher(http, handler.RunId);
        var workflow = Workflow.Define("artifacts", "1", w => w.Type("test").Node("n", n => n.Task("t", async ctx =>
            await ctx.Artifacts.PublishAsync("raw.bin", input, "application/octet-stream", Token))));
        var failure = await Record.ExceptionAsync(() => new TaskRegistry(workflow).ExecuteAsync("n/t", new(Token, artifacts: publisher)));
        if (cancel) Assert.IsType<OperationCanceledException>(failure);
        else Assert.IsType<IOException>(failure);
        Assert.Empty(handler.Methods);
        Assert.True(input.CanRead);
        Assert.Empty(Directory.GetFiles(Directory.GetCurrentDirectory(), ".wf-artifact-*"));
    }

    private static ArtifactPublisher Publisher(HttpClient http, Guid runId) => new(http, new UploadGrant
    {
        Kind = "artifacts", UploadUri = "https://engine.test" + ArtifactMetadata.GrantPath,
        BearerToken = "valid.capability", MaxBytes = ArtifactMetadata.RunBudget,
        ExpiresAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(10))
    }, new AttemptRef { RunId = runId.ToString("D"), NodeKey = "n", TaskKey = "t", Attempt = 1 });

    private sealed class Handler(byte[] expected, string scenario = "") : HttpMessageHandler
    {
        public Guid RunId { get; } = Guid.CreateVersion7();
        public Guid Id { get; private set; }
        public List<string> Methods { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Methods.Add(request.Method.Method);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("valid.capability", request.Headers.Authorization.Parameter);
            var id = Guid.Parse(request.RequestUri!.Segments[^1]);
            if (request.Method == HttpMethod.Delete) return new(HttpStatusCode.NoContent);
            if (request.Method == HttpMethod.Post)
            {
                Id = id;
                if (scenario == "oversized-grant") return Oversized();
                var metadata = await request.Content!.ReadFromJsonAsync(ArtifactJsonContext.Default.ArtifactMetadata, token);
                Assert.Equal(expected.Length, metadata!.SizeBytes);
                Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(expected)), metadata.Sha256);
                return new(HttpStatusCode.Created)
                {
                    Content = JsonContent.Create(new ArtifactGrant(scenario == "bad-grant" ? Guid.CreateVersion7() : id,
                        DateTimeOffset.UtcNow.AddMinutes(10), false), ArtifactJsonContext.Default.ArtifactGrant)
                };
            }
            Assert.Equal(Id, id);
            Assert.Equal(expected.Length, request.Content!.Headers.ContentLength);
            Assert.Equal(expected, await request.Content.ReadAsByteArrayAsync(token));
            if (scenario == "oversized-receipt") return Oversized();
            return new(scenario == "server-error" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Created)
            {
                Content = JsonContent.Create(new ArtifactReceipt(scenario == "bad-receipt" ? Guid.CreateVersion7() : RunId, Id),
                    ArtifactJsonContext.Default.ArtifactReceipt)
            };
        }

        private static HttpResponseMessage Oversized() => new(HttpStatusCode.Created)
        {
            Content = new StringContent(new string('x', 4097), System.Text.Encoding.UTF8, "application/json")
        };
    }

    private sealed class Input(byte[] bytes, bool nonseekable) : MemoryStream(bytes)
    {
        public override bool CanSeek => !nonseekable;
        public int LargestRead { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            LargestRead = Math.Max(LargestRead, buffer.Length);
            return base.ReadAsync(buffer, cancellationToken);
        }
    }
    private sealed class BrokenInput(bool cancel) : MemoryStream(new byte[256 * 1024])
    {
        public override bool CanSeek => false;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Position == 0 ? base.ReadAsync(buffer, cancellationToken) :
            ValueTask.FromException<int>(cancel ? new OperationCanceledException() : new IOException("Injected input failure."));
    }
    private sealed class Unreadable : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Input must not be read.");
    }
    private sealed class Huge : MemoryStream
    {
        public override long Length => ArtifactMetadata.RunBudget + 1;
    }
}
