using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Wf.Contracts;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Reports;
using Wf.Engine.Core.Results;

namespace Wf.Sdk;

internal sealed class ReportUploadClient(HttpClient http, Uri endpoint, string bearerToken, ResultIdentity attempt)
{
    internal static ReportPublisher Create(HttpClient http, UploadGrant grant, AttemptRef reference)
    {
        var identity = ResultDeduplicator.Identity(reference);
        if (grant.Kind != "reports" || grant.MaxBytes == 0 || grant.ExpiresAt is null ||
            grant.ExpiresAt.ToDateTimeOffset() <= DateTimeOffset.UtcNow ||
            !Uri.TryCreate(grant.UploadUri, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme is not ("http" or "https") || endpoint.UserInfo.Length != 0 ||
            endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 ||
            endpoint.AbsolutePath != ReportUploadMetadata.EndpointPath ||
            string.IsNullOrEmpty(grant.BearerToken) || grant.BearerToken.Length > 4096 ||
            grant.BearerToken.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.')))
            throw new InvalidDataException("Invalid report upload capability supplied by the agent.");
        // The trusted agent selects the configured engine. Its server policy is authoritative across hosts.
        return new(new ReportUploadClient(http, endpoint, grant.BearerToken, identity).PublishAsync,
            new ReportLimits(int.MaxValue, long.MaxValue));
    }

    private async Task PublishAsync(ReportPublication publication, CancellationToken cancellationToken)
    {
        if (publication.SourceKey != attempt.TaskKey)
            throw new InvalidOperationException("Report source does not match the dispatched attempt.");
        var reportId = Guid.CreateVersion7();
        var metadata = new ReportUploadMetadata(publication.Kind, publication.Title, publication.Tags.ToArray(),
            Convert.ToHexStringLower(SHA256.HashData(publication.Payload.Span)), publication.Payload.Length);
        metadata.Validate();
        using var request = new HttpRequestMessage(HttpMethod.Post,
            endpoint.AbsoluteUri.TrimEnd('/') + "/" + reportId.ToString("D"))
        {
            Content = new ReadOnlyMemoryContent(publication.Payload),
            Version = http.DefaultRequestVersion,
            VersionPolicy = http.DefaultVersionPolicy
        };
        request.Content.Headers.ContentType = new("application/octet-stream");
        request.Headers.Authorization = new("Bearer", bearerToken);
        request.Headers.Add(ReportUploadMetadata.HeaderName, WebEncoders.Base64UrlEncode(
            JsonSerializer.SerializeToUtf8Bytes(metadata, ReportJsonContext.Default.ReportUploadMetadata)));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.RequestEntityTooLarge)
            throw new ReportLimitExceededException("Report publication exceeds the server's per-run count or per-report byte limit.");
        if (response.StatusCode != HttpStatusCode.Created)
            throw new HttpRequestException($"Report storage rejected publication (HTTP {(int)response.StatusCode}).",
                null, response.StatusCode);
        if (response.Content.Headers.ContentType?.MediaType != "application/json" ||
            response.Content.Headers.ContentLength > 4096)
            throw new InvalidDataException("Report storage returned an invalid receipt.");
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        var bytes = new byte[4097];
        var length = 0;
        while (length < bytes.Length)
        {
            var read = await body.ReadAsync(bytes.AsMemory(length), cancellationToken);
            if (read == 0)
                break;
            length += read;
        }
        if (length > 4096)
            throw new InvalidDataException("Report storage returned an oversized receipt.");
        ReportReceipt? receipt;
        try { receipt = JsonSerializer.Deserialize(bytes.AsSpan(0, length), ReportJsonContext.Default.ReportReceipt); }
        catch (JsonException error) { throw new InvalidDataException("Report storage returned an invalid receipt.", error); }
        if (receipt?.RunId != attempt.RunId || receipt.ReportId != reportId)
            throw new InvalidDataException("Report storage returned a receipt for different content.");
    }
}
