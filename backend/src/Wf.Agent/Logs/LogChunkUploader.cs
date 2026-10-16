using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Logs;
using Wf.Contracts.Registration;

namespace Wf.Agent.Logs;

/// <summary>The dispatch grant authorizes acquisition only for its attempt prefix. Every PUT obtains a
/// fresh, short-lived, exact-name/length/hash grant. Retries reuse immutable names and bytes.</summary>
public sealed class LogChunkUploader(HttpClient http, TimeProvider clock, ILogger<LogChunkUploader> logger)
{
    public async Task UploadAsync(LogSpooler spool, UploadGrant grant, Uri engine,
        CancellationToken cancellationToken = default)
    {
        ValidateUri(engine);
        var acquire = Resolve(engine, grant.UploadUri);
        if (grant.Kind != "logs" || grant.MaxBytes == 0 || grant.ExpiresAt is null ||
            Expired(grant) ||
            acquire.GetLeftPart(UriPartial.Authority) != engine.GetLeftPart(UriPartial.Authority))
            throw new InvalidDataException("Invalid attempt log upload capability.");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = await spool.SnapshotAsync(cancellationToken);
            var head = Math.Min(snapshot.HeadBytes, Math.Min(snapshot.MaxBytes, snapshot.SpoolQuotaBytes) / 2);
            foreach (var chunk in snapshot.Chunks.DistinctBy(chunk => chunk.Number).Where(chunk => !chunk.Uploaded &&
                (!snapshot.Truncated || snapshot.Complete || chunk.ByteStart < head)))
            {
                var path = Path.Combine(spool.DirectoryPath, chunk.Name);
                try { await UploadFileAsync(path, chunk.Name, chunk.Sha256, chunk.CompressedBytes); }
                catch (FileNotFoundException)
                {
                    if ((await spool.SnapshotAsync(cancellationToken)).Chunks.All(item => item.Number != chunk.Number)) continue;
                    throw;
                }
                await spool.AcknowledgeAsync(chunk.Number);
            }
            snapshot = await spool.SnapshotAsync(cancellationToken);
            if (snapshot.Complete && snapshot.Chunks.All(chunk => chunk.Uploaded))
            {
                if (spool.IsUploaded) return;
                var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, LogJsonContext.Default.LogIndex);
                var path = Path.Combine(spool.DirectoryPath, "upload-index.json");
                LogSpooler.DurableWrite(path, bytes);
                await UploadFileAsync(path, "index.json",
                    Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), bytes.Length);
                await spool.AcknowledgeIndexAsync();
                File.Delete(path);
                return;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(200), clock, cancellationToken);
        }

        async Task UploadFileAsync(string path, string name, string hash, long length)
        {
            if ((ulong)length > grant.MaxBytes)
                throw new InvalidDataException("Log object exceeds its upload capability.");
            var retry = 0;
            while (true)
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, acquire)
                    {
                        Version = http.DefaultRequestVersion, VersionPolicy = http.DefaultVersionPolicy,
                        Content = JsonContent.Create(new LogUploadRequest(name, hash, length), LogJsonContext.Default.LogUploadRequest)
                    };
                    using var response = await http.SendAsync(request, cancellationToken);
                    response.EnsureSuccessStatusCode();
                    var upload = await response.Content.ReadFromJsonAsync(RegistrationJsonContext.Default.UploadGrantResponse,
                        cancellationToken) ?? throw new InvalidDataException("Missing log upload grant.");
                    if (upload.Exists) return;
                    if (upload.UploadUri is null || upload.MaxBytes is null || upload.MaxBytes < length ||
                        upload.ExpiresAt is null || upload.ExpiresAt <= clock.GetUtcNow())
                        throw new InvalidDataException("Invalid log object upload grant.");
                    var uri = Resolve(engine, upload.UploadUri);
                    if (uri.GetLeftPart(UriPartial.Authority) != acquire.GetLeftPart(UriPartial.Authority))
                        throw new InvalidDataException("Log upload redirected outside the trusted engine.");
                    await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                        64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    using var put = new HttpRequestMessage(HttpMethod.Put, uri)
                    {
                        Version = http.DefaultRequestVersion, VersionPolicy = http.DefaultVersionPolicy,
                        Content = new StreamContent(input)
                    };
                    put.Content.Headers.ContentLength = length;
                    using var uploaded = await http.SendAsync(put, cancellationToken);
                    uploaded.EnsureSuccessStatusCode();
                    return;
                }
                catch (Exception error) when (IsTransient(error, cancellationToken))
                {
                    // Deliberately omit exception/URI: signed grants are bearer credentials.
                    logger.LogWarning("Log upload temporarily unavailable; durable chunk retained (retry {Retry}).", ++retry);
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 1 << Math.Min(retry - 1, 5))), clock, cancellationToken);
                }
            }
        }
    }

    private static bool IsTransient(Exception error, CancellationToken token) => error switch
    {
        HttpRequestException { StatusCode: null or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError } => true,
        OperationCanceledException when !token.IsCancellationRequested => true,
        _ => false
    };

    private bool Expired(UploadGrant grant)
    {
        try { return grant.ExpiresAt.ToDateTimeOffset() <= clock.GetUtcNow(); }
        catch (InvalidOperationException) { throw new InvalidDataException("Invalid attempt log capability expiry."); }
    }

    private static Uri Resolve(Uri engine, string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(engine, value, out var uri))
            throw new InvalidDataException("Invalid log upload URL.");
        ValidateUri(uri);
        return uri;
    }

    private static void ValidateUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.Scheme == "http" && !uri.IsLoopback)
            throw new InvalidDataException("Log upload requires HTTPS (HTTP only on loopback).");
    }
}
