using System.Net;
using System.Security.Cryptography;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.WebUtilities;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Checkpoints;
using Wf.Contracts;
using Wf.Contracts.Definition;

namespace Wf.Sdk;

internal static class CheckpointUploadClient
{
    internal static CheckpointStore Create(HttpClient http, Dispatch dispatch,
        Func<CheckpointSaved, CancellationToken, Task> acknowledge, CancellationToken attemptCancellation)
    {
        var grant = dispatch.Grants.SingleOrDefault(grant => grant.Kind == "checkpoints");
        CheckpointReference? initial = dispatch.Checkpoint is null ? null :
            CheckpointProtocol.ValidateSeed(dispatch.Checkpoint, dispatch.Task);
        if (grant is not null && (grant.MaxBytes != CheckpointProtocol.MaximumBytes || grant.ExpiresAt is null ||
            grant.ExpiresAt.ToDateTimeOffset() <= DateTimeOffset.UtcNow ||
            !Uri.TryCreate(grant.UploadUri, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme is not ("http" or "https") || endpoint.UserInfo.Length != 0 ||
            endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 ||
            endpoint.AbsolutePath != CheckpointProtocol.EndpointPath ||
            string.IsNullOrEmpty(grant.BearerToken) || grant.BearerToken.Length > 8192 ||
            grant.BearerToken.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))))
            throw new InvalidDataException("Invalid checkpoint upload capability supplied by the agent.");
        return new(grant is null ? null : Save,
            dispatch.Checkpoint?.StateJson.Memory ?? default, initial?.Sequence ?? 0, initial?.SavedAt,
            cancellationToken: attemptCancellation);

        async Task Save(ReadOnlyMemory<byte> bytes, long sequence, DateTimeOffset at, CancellationToken token)
        {
            var sha = Convert.ToHexStringLower(SHA256.HashData(bytes.Span));
            var saved = new CheckpointSaved
            {
                Task = dispatch.Task.Clone(), ReservationEpoch = dispatch.ReservationEpoch,
                CheckpointSequence = checked((ulong)sequence), SavedAt = Timestamp.FromDateTimeOffset(at),
                Sha256 = sha, SizeBytes = (ulong)bytes.Length,
                BlobKey = CheckpointProtocol.Key(Guid.Parse(dispatch.Task.RunId),
                    $"{dispatch.Task.NodeKey}/{dispatch.Task.TaskKey}", sequence, sha)
            };
            _ = CheckpointProtocol.Validate(saved);
            using var request = new HttpRequestMessage(HttpMethod.Put, grant!.UploadUri)
            {
                Content = new ReadOnlyMemoryContent(bytes), Version = http.DefaultRequestVersion,
                VersionPolicy = http.DefaultVersionPolicy
            };
            request.Content.Headers.ContentType = new("application/json");
            request.Headers.Authorization = new("Bearer", grant.BearerToken);
            request.Headers.Add(CheckpointProtocol.MetadataHeader, WebEncoders.Base64UrlEncode(saved.ToByteArray()));
            try
            {
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                if (response.StatusCode != HttpStatusCode.Created)
                    throw new TaskFailureException($"Checkpoint storage rejected state (HTTP {(int)response.StatusCode}).",
                        retryable: (int)response.StatusCode >= 500 ||
                            response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests,
                        category: FailureCategory.PlatformError);
            }
            catch (Exception error) when (error is HttpRequestException or IOException ||
                error is OperationCanceledException && !token.IsCancellationRequested)
            {
                throw new TaskFailureException("Checkpoint storage is temporarily unavailable.",
                    category: FailureCategory.PlatformError, innerException: error);
            }
            await acknowledge(saved, token);
        }
    }
}
