using System.Buffers;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Artifacts;
using Wf.Engine.Core.Results;

namespace Wf.Sdk;

public sealed class ArtifactPublisher : IArtifacts
{
    private readonly HttpClient http;
    private readonly Uri endpoint;
    private readonly string capability;
    private readonly ResultIdentity attempt;
    private readonly string? sourceKey;

    public ArtifactPublisher(HttpClient http, UploadGrant grant, AttemptRef reference)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentNullException.ThrowIfNull(reference);
        attempt = ResultDeduplicator.Identity(reference);
        if (grant.Kind != "artifacts" || grant.MaxBytes != ArtifactMetadata.RunBudget ||
            grant.ExpiresAt is null || grant.ExpiresAt.ToDateTimeOffset() <= DateTimeOffset.UtcNow ||
            !Uri.TryCreate(grant.UploadUri, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 || uri.Query.Length != 0 ||
            uri.Fragment.Length != 0 || uri.AbsolutePath != ArtifactMetadata.GrantPath ||
            string.IsNullOrEmpty(grant.BearerToken) || grant.BearerToken.Length > 4096 ||
            grant.BearerToken.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')))
            throw new InvalidDataException("Invalid artifact capability supplied by the agent.");
        this.http = http;
        endpoint = uri;
        capability = grant.BearerToken;
    }

    private ArtifactPublisher(ArtifactPublisher publisher, string sourceKey)
    {
        http = publisher.http;
        endpoint = publisher.endpoint;
        capability = publisher.capability;
        attempt = publisher.attempt;
        this.sourceKey = sourceKey;
    }

    internal ArtifactPublisher ForSource(string key) => new(this, key);

    public async Task<Uri> PublishAsync(string name, Stream content, string contentType, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        new ArtifactMetadata(name, contentType, 0, new string('0', 64)).Validate();
        if (sourceKey != attempt.TaskKey)
            throw new InvalidOperationException("Artifact source does not match the dispatched attempt.");
        cancellationToken.ThrowIfCancellationRequested();
        if (content.CanSeek && content.Length - content.Position > ArtifactMetadata.RunBudget)
            throw new ArtifactLimitExceededException();
        // IBlobStore verifies an expected hash. Seekable inputs need no staging; non-seekable
        // inputs use a private, delete-on-close file instead of buffering an artifact in RAM.
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.None,
            Options = FileOptions.Asynchronous | FileOptions.DeleteOnClose
        };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var staged = content.CanSeek ? null : new FileStream(
            Path.Combine(Directory.GetCurrentDirectory(), ".wf-artifact-" + Guid.NewGuid().ToString("N")), options);
        var input = (Stream?)staged ?? content;
        var start = input.Position;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        long size = 0;
        try
        {
            while (true)
            {
                var read = await content.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length,
                    ArtifactMetadata.RunBudget - size + 1)), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                size += read;
                if (size > ArtifactMetadata.RunBudget) throw new ArtifactLimitExceededException();
                hash.AppendData(buffer, 0, read);
                if (staged is not null) await staged.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        input.Position = start;
        var metadata = new ArtifactMetadata(name, contentType, size, Convert.ToHexStringLower(hash.GetHashAndReset()));
        var id = Guid.CreateVersion7();
        var grantUri = new Uri(endpoint.AbsoluteUri + "/" + id.ToString("D"));
        // A lost grant response can still leave a durable reservation.
        var granted = true;
        try
        {
            using (var grant = Request(HttpMethod.Post, grantUri,
                JsonContent.Create(metadata, ArtifactJsonContext.Default.ArtifactMetadata)))
            using (var response = await http.SendAsync(grant, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                Check(response);
                var receipt = await Read(response, ArtifactJsonContext.Default.ArtifactGrant, cancellationToken).ConfigureAwait(false);
                if (receipt.ArtifactId != id || receipt.ExpiresAt <= DateTimeOffset.UtcNow)
                    throw new InvalidDataException("Invalid artifact grant receipt.");
            }
            using var upload = Request(HttpMethod.Put, new Uri(endpoint, ArtifactMetadata.PublishPath + "/" + id.ToString("D")),
                new StreamContent(new BorrowedStream(input)));
            upload.Content!.Headers.ContentType = new("application/octet-stream");
            upload.Content.Headers.ContentLength = size;
            using var published = await http.SendAsync(upload, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            Check(published);
            var result = await Read(published, ArtifactJsonContext.Default.ArtifactReceipt, cancellationToken).ConfigureAwait(false);
            if (result.RunId != attempt.RunId || result.ArtifactId != id)
                throw new InvalidDataException("Artifact receipt refers to different content.");
            granted = false;
            return new Uri(endpoint, "/api/v1/artifacts/" + id.ToString("D") + "/content");
        }
        finally
        {
            if (granted)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    using var release = Request(HttpMethod.Delete, grantUri);
                    using var response = await http.SendAsync(release, cleanup.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is HttpRequestException or OperationCanceledException) { }
            }
        }
    }

    private HttpRequestMessage Request(HttpMethod method, Uri uri, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, uri)
        {
            Content = content, Version = http.DefaultRequestVersion, VersionPolicy = http.DefaultVersionPolicy
        };
        request.Headers.Authorization = new("Bearer", capability);
        return request;
    }

    private static void Check(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.RequestEntityTooLarge) throw new ArtifactLimitExceededException();
        if (response.StatusCode != HttpStatusCode.Created)
            throw new HttpRequestException($"Artifact storage rejected publication (HTTP {(int)response.StatusCode}).", null, response.StatusCode);
    }

    private static async Task<T> Read<T>(HttpResponseMessage response,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type, CancellationToken token)
    {
        if (response.Content.Headers.ContentType?.MediaType != "application/json" ||
            response.Content.Headers.ContentLength > 4096) throw new InvalidDataException("Invalid artifact receipt.");
        await using var body = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var buffer = new byte[4097];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await body.ReadAsync(buffer.AsMemory(count), token).ConfigureAwait(false);
            if (read == 0) break;
            count += read;
        }
        if (count > 4096) throw new InvalidDataException("Oversized artifact receipt.");
        try { return System.Text.Json.JsonSerializer.Deserialize(buffer.AsSpan(0, count), type) ?? throw new InvalidDataException("Empty artifact receipt."); }
        catch (System.Text.Json.JsonException error) { throw new InvalidDataException("Invalid artifact receipt.", error); }
    }

    private sealed class BorrowedStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
