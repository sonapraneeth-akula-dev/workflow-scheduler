using Azure;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;

namespace Wf.ControlPlane.Storage;

/// <summary>
/// Uses a configured SDK client (managed identity in Azure). Container is provisioned externally and private.
/// SAS writes only staging; caller metadata is never evidence of integrity.
/// </summary>
public sealed class AzureBlobStore : IBlobStore
{
    private const int TransferSize = 1024 * 1024;
    private readonly BlobServiceClient service;
    private readonly BlobContainerClient container;
    private readonly long maximumBytes;
    private readonly TimeProvider time;
    private readonly object grantOwner = new();

    public AzureBlobStore(BlobServiceClient serviceClient, string containerName,
        long maximumBytes = BlobStreaming.DefaultMaximumBytes, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(serviceClient);
        if (maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        // HTTP is permitted only for an explicitly configured loopback emulator.
        if (serviceClient.Uri.Scheme != Uri.UriSchemeHttps && !serviceClient.Uri.IsLoopback)
            throw new ArgumentException("Azure storage requires HTTPS.", nameof(serviceClient));
        service = serviceClient;
        container = service.GetBlobContainerClient(containerName);
        this.maximumBytes = maximumBytes;
        time = timeProvider ?? TimeProvider.System;
    }

    public async Task UploadAsync(BlobContent content, Stream source, CancellationToken cancellationToken = default)
    {
        BlobStreaming.Validate(content, maximumBytes);
        var staging = container.GetBlobClient($".staging/{Guid.NewGuid():N}");
        try
        {
            await using (var output = await staging.OpenWriteAsync(overwrite: true,
                new BlobOpenWriteOptions { BufferSize = TransferSize }, cancellationToken))
            {
                await BlobStreaming.CopyVerifiedAsync(source, output, content, maximumBytes, cancellationToken);
            }
            var properties = await staging.GetPropertiesAsync(cancellationToken: cancellationToken);
            await PromoteAsync(content, staging, properties.Value.ETag, cancellationToken);
        }
        finally { await DeleteStagingAsync(staging); }
    }

    public async Task DownloadAsync(BlobContent content, Stream destination, CancellationToken cancellationToken = default)
    {
        BlobStreaming.Validate(content, maximumBytes);
        var blob = container.GetBlobClient(content.Key);
        var properties = await blob.GetPropertiesAsync(cancellationToken: cancellationToken);
        CheckLength(content, properties.Value);
        await using var input = await OpenPinnedAsync(blob, properties.Value.ETag, cancellationToken);
        await BlobStreaming.CopyVerifiedAsync(input, destination, content, maximumBytes, cancellationToken);
    }

    public async Task<Stream> OpenLogReadAsync(string key, long maximumBytes, CancellationToken cancellationToken = default)
    {
        BlobStreaming.ValidateLogRead(key, maximumBytes);
        var blob = container.GetBlobClient(key);
        try
        {
            var properties = await blob.GetPropertiesAsync(cancellationToken: cancellationToken);
            if (properties.Value.ContentLength > Math.Min(maximumBytes, this.maximumBytes))
                throw new InvalidDataException("Named log object exceeds its byte limit.");
            return await OpenPinnedAsync(blob, properties.Value.ETag, cancellationToken);
        }
        catch (RequestFailedException error) when (error.Status == 404)
        {
            throw new FileNotFoundException("Named log object is unavailable.", error);
        }
    }

    /// <summary>
    /// Issues create-only, single-PUT SAS for one random staging blob, valid at most 15 minutes (5,000 MiB limit).
    /// MaxBytes is advisory at Azure's direct-write boundary: CompleteUploadAsync enforces it before promotion.
    /// Keep the returned object in trusted server state, exposing only UploadUri/MaxBytes/expiry to the writer.
    /// </summary>
    public async Task<AzureBlobUploadGrant> CreateUploadGrantAsync(BlobContent content, long maxBytes,
        TimeSpan lifetime, CancellationToken cancellationToken = default)
    {
        BlobStreaming.Validate(content, maximumBytes);
        cancellationToken.ThrowIfCancellationRequested();
        if (maxBytes < content.SizeBytes || maxBytes > maximumBytes || maxBytes > 5000L * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        var expires = BlobStreaming.Expiry(time, lifetime);
        var staging = container.GetBlobClient($".staging/{Guid.NewGuid():N}");
        var sas = new BlobSasBuilder
        {
            BlobContainerName = container.Name, BlobName = staging.Name, Resource = "b",
            ExpiresOn = expires,
            Protocol = service.Uri.IsLoopback ? SasProtocol.HttpsAndHttp : SasProtocol.Https
        };
        sas.SetPermissions(BlobSasPermissions.Create);
        Uri uri;
        if (staging.CanGenerateSasUri)
        {
            // Explicitly configured SharedKey SDK clients support Azurite; production should use Entra.
            uri = staging.GenerateSasUri(sas);
        }
        else
        {
            var key = await service.GetUserDelegationKeyAsync(time.GetUtcNow().AddMinutes(-15), expires,
                cancellationToken);
            uri = staging.GenerateUserDelegationSasUri(sas, key.Value);
        }
        return new AzureBlobUploadGrant(grantOwner, content, staging.Name, uri, maxBytes, expires);
    }

    /// <summary>
    /// Must succeed before any pointer/registration is persisted. Reads actual bytes, ignores SHA metadata,
    /// pins the verified ETag, then conditionally creates immutable final content.
    /// </summary>
    public async Task CompleteUploadAsync(AzureBlobUploadGrant grant, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(grant);
        if (!ReferenceEquals(grant.Owner, grantOwner))
            throw new UnauthorizedAccessException("Upload grant belongs to a different store instance.");
        var staging = container.GetBlobClient(grant.StagingName);
        try
        {
            CheckExpiry(grant);
            var properties = await staging.GetPropertiesAsync(cancellationToken: cancellationToken);
            CheckLength(grant.Content, properties.Value);
            if (properties.Value.ContentLength > grant.MaxBytes)
                throw new InvalidDataException("Blob exceeds its upload grant.");
            await using (var input = await OpenPinnedAsync(staging, properties.Value.ETag, cancellationToken))
            {
                await BlobStreaming.CopyVerifiedAsync(input, Stream.Null, grant.Content, maximumBytes, cancellationToken);
            }
            CheckExpiry(grant);
            await PromoteAsync(grant.Content, staging, properties.Value.ETag, cancellationToken);
        }
        finally { await DeleteStagingAsync(staging); }
    }

    private void CheckExpiry(AzureBlobUploadGrant grant)
    {
        if (grant.ExpiresOn <= time.GetUtcNow()) throw new UnauthorizedAccessException("Upload grant expired.");
    }

    private async Task PromoteAsync(BlobContent content, BlobClient staging, ETag etag,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var input = await OpenPinnedAsync(staging, etag, cancellationToken);
        try
        {
            await container.GetBlobClient(content.Key).UploadAsync(input, new BlobUploadOptions
            {
                Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                TransferOptions = new StorageTransferOptions
                {
                    MaximumConcurrency = 1, InitialTransferSize = TransferSize, MaximumTransferSize = TransferSize
                }
            }, cancellationToken);
        }
        catch (RequestFailedException error) when (error.ErrorCode is "BlobAlreadyExists" or "ConditionNotMet")
        {
            await DownloadAsync(content, Stream.Null, cancellationToken);
        }
    }

    private static Task<Stream> OpenPinnedAsync(BlobClient blob, ETag etag, CancellationToken cancellationToken) =>
        blob.OpenReadAsync(new BlobOpenReadOptions(allowModifications: false)
        {
            BufferSize = TransferSize, Conditions = new BlobRequestConditions { IfMatch = etag }
        }, cancellationToken);

    private static async Task DeleteStagingAsync(BlobClient staging)
    {
        // Cleanup must still run when the request was cancelled, but must not wait indefinitely.
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await staging.DeleteIfExistsAsync(cancellationToken: cleanup.Token);
    }

    private static void CheckLength(BlobContent content, BlobProperties properties)
    {
        if (properties.ContentLength != content.SizeBytes)
            throw new InvalidDataException("Blob length does not match its declared byte count.");
    }
}

public sealed class AzureBlobUploadGrant
{
    internal AzureBlobUploadGrant(object owner, BlobContent content, string stagingName, Uri uploadUri,
        long maxBytes, DateTimeOffset expiresOn)
    {
        Owner = owner;
        Content = content;
        StagingName = stagingName;
        UploadUri = uploadUri;
        MaxBytes = maxBytes;
        ExpiresOn = expiresOn;
    }

    internal object Owner { get; }
    internal string StagingName { get; }
    public BlobContent Content { get; }
    public Uri UploadUri { get; }
    public long MaxBytes { get; }
    public DateTimeOffset ExpiresOn { get; }
}
