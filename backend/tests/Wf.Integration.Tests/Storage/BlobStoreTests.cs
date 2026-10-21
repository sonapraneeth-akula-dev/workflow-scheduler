using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Wf.ControlPlane.Storage;

namespace Wf.Integration.Tests.Storage;

public sealed class BlobStoreTests(BlobStoreEnvironment environment) : IClassFixture<BlobStoreEnvironment>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly byte[] Data = Encoding.UTF8.GetBytes("portable streamed content");

    [Fact]
    public async Task Configured_replicas_share_verified_blobs_without_a_shared_filesystem()
    {
        await using var scope = await environment.CreateAsync(azure: true);
        var container = scope.ContainerUri!.Segments[^1];
        var first = environment.ConfiguredStore(container);
        var second = environment.ConfiguredStore(container);
        var content = Content(Data);
        await first.UploadAsync(content, new MemoryStream(Data, writable: false), Token);
        using var downloaded = new MemoryStream();
        await second.DownloadAsync(content, downloaded, Token);
        Assert.Equal(Data, downloaded.ToArray());
        await second.UploadAsync(content, new MemoryStream(Data, writable: false), Token);
        await scope.AssertNoStagingAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BlobStore_round_trips_and_identical_concurrent_writes_are_idempotent(bool azure)
    {
        await using var scope = await environment.CreateAsync(azure);
        var content = Content(Data);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            using var source = new MemoryStream(Data, writable: false);
            await scope.Store.UploadAsync(content, source, Token);
            Assert.True(source.CanRead);
        }));
        using var output = new MemoryStream();
        await scope.Store.DownloadAsync(content, output, Token);
        Assert.Equal(Data, output.ToArray());
        Assert.True(output.CanWrite);
        await scope.AssertNoStagingAsync();
    }

    [Theory]
    [InlineData(false, -1)]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(true, -1)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    public async Task BlobStore_rejects_truncation_hash_mismatch_and_extra_bytes_without_final_blob(bool azure, int delta)
    {
        await using var scope = await environment.CreateAsync(azure);
        var bytes = delta switch
        {
            -1 => Data[..^1],
            0 => Enumerable.Repeat((byte)'x', Data.Length).ToArray(),
            _ => [.. Data, 42]
        };
        using var input = new MemoryStream(bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => scope.Store.UploadAsync(Content(Data), input, Token));
        Assert.False(await scope.ExistsAsync(Content(Data)));
        await scope.AssertNoStagingAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BlobStore_mismatch_never_replaces_existing_valid_content(bool azure)
    {
        await using var scope = await environment.CreateAsync(azure);
        var content = Content(Data);
        using var valid = new MemoryStream(Data);
        await scope.Store.UploadAsync(content, valid, Token);
        using var invalid = new MemoryStream(new byte[Data.Length]);
        await Assert.ThrowsAsync<InvalidDataException>(() => scope.Store.UploadAsync(content, invalid, Token));
        using var output = new MemoryStream();
        await scope.Store.DownloadAsync(content, output, Token);
        Assert.Equal(Data, output.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BlobStore_enforces_configured_limits_before_reading(bool azure)
    {
        await using var scope = await environment.CreateAsync(azure, maximumBytes: Data.Length - 1);
        using var input = new PatternStream(Data.Length, throwOnRead: true);
        await Assert.ThrowsAsync<InvalidDataException>(() => scope.Store.UploadAsync(Content(Data), input, Token));
        Assert.Equal(0, input.BytesRead);
        Assert.False(await scope.ExistsAsync(Content(Data)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BlobStore_cancellation_mid_upload_cleans_staging_and_never_publishes(bool azure)
    {
        await using var scope = await environment.CreateAsync(azure);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var reads = 0;
        using var input = new PatternStream(4 * 1024 * 1024, afterRead: () =>
        {
            if (++reads == 20) cancel.Cancel();
        });
        var content = new BlobContent(BlobKind.Artifacts, new string('a', 64), input.TotalLength);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.Store.UploadAsync(content, input, cancel.Token));
        Assert.False(await scope.ExistsAsync(content));
        await scope.AssertNoStagingAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BlobStore_large_concurrent_uploads_use_atomic_conditional_publication(bool azure)
    {
        const int size = 3 * 1024 * 1024;
        await using var scope = await environment.CreateAsync(azure);
        using var hashInput = new PatternStream(size);
        var content = new BlobContent(BlobKind.Reports,
            Convert.ToHexString(await SHA256.HashDataAsync(hashInput, Token)), size);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            using var input = new PatternStream(size);
            await scope.Store.UploadAsync(content, input, Token);
        }));
        await scope.Store.DownloadAsync(content, Stream.Null, Token);
        await scope.AssertNoStagingAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BlobStore_source_io_failure_cannot_publish_partial_content(bool azure)
    {
        await using var scope = await environment.CreateAsync(azure);
        using var source = new PatternStream(Data.Length, throwOnRead: true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Store.UploadAsync(Content(Data), source, Token));
        Assert.True(source.CanRead);
        Assert.False(await scope.ExistsAsync(Content(Data)));
        await scope.AssertNoStagingAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BlobStore_download_detects_corrupt_bytes_and_length_and_rejects_corrupt_existing_final(bool azure)
    {
        await using var scope = await environment.CreateAsync(azure);
        var content = Content(Data);
        await scope.CorruptAsync(content, new byte[Data.Length]);
        await Assert.ThrowsAsync<InvalidDataException>(() => scope.Store.DownloadAsync(content, Stream.Null, Token));
        using var valid = new MemoryStream(Data);
        await Assert.ThrowsAsync<InvalidDataException>(() => scope.Store.UploadAsync(content, valid, Token));
        await scope.CorruptAsync(content, Data[..^1]);
        await Assert.ThrowsAsync<InvalidDataException>(() => scope.Store.DownloadAsync(content, Stream.Null, Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BlobStore_streams_large_nonseekable_content_with_bounded_read_requests(bool azure)
    {
        const int size = 20 * 1024 * 1024;
        await using var scope = await environment.CreateAsync(azure, maximumBytes: size);
        using var hashInput = new PatternStream(size);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(hashInput, Token));
        using var input = new PatternStream(size);
        var content = new BlobContent(BlobKind.Packages, hash, size);
        await scope.Store.UploadAsync(content, input, Token);
        await scope.Store.DownloadAsync(content, Stream.Null, Token);
        Assert.Equal(size, input.BytesRead);
        Assert.InRange(input.LargestRead, 1, 128 * 1024);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BlobStore_supports_empty_objects_and_download_cancellation(bool azure)
    {
        await using var scope = await environment.CreateAsync(azure);
        var content = Content([]);
        await scope.Store.UploadAsync(content, Stream.Null, Token);
        await scope.Store.DownloadAsync(content, Stream.Null, Token);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            scope.Store.DownloadAsync(content, Stream.Null, cancel.Token));
    }

    [Fact]
    public async Task BlobStore_filesystem_grant_verifies_content_and_replay_is_identical_content_only()
    {
        await using var scope = await environment.CreateAsync(false);
        var store = (FileSystemBlobStore)scope.Store;
        var content = Content(Data);
        var grant = store.CreateUploadGrant(content, Data.Length, TimeSpan.FromMinutes(1));
        for (var i = 0; i < 2; i++)
        {
            using var input = new MemoryStream(Data);
            await store.UploadWithGrantAsync(grant, input, Token);
        }
        using var bad = new MemoryStream(new byte[Data.Length]);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.UploadWithGrantAsync(grant, bad, Token));
        using var extra = new MemoryStream([.. Data, 42]);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.UploadWithGrantAsync(grant, extra, Token));
        await store.DownloadAsync(content, Stream.Null, Token);
        await scope.AssertNoStagingAsync();
    }

    [Theory]
    [InlineData("Key", "../../escape")]
    [InlineData("Sha256", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("SizeBytes", 1)]
    [InlineData("MaxBytes", 999)]
    [InlineData("ExpiresUtcTicks", 1)]
    public async Task BlobStore_filesystem_rejects_tampered_grant_fields(string field, object replacement)
    {
        await using var scope = await environment.CreateAsync(false);
        var store = (FileSystemBlobStore)scope.Store;
        var grant = store.CreateUploadGrant(Content(Data), Data.Length, TimeSpan.FromMinutes(1));
        var parts = grant.Split('.');
        var payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(WebEncoders.Base64UrlDecode(parts[0]))!;
        payload[field] = JsonSerializer.SerializeToElement(replacement);
        var tampered = $"{WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(payload))}.{parts[1]}";
        using var input = new MemoryStream(Data);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.UploadWithGrantAsync(tampered, input, Token));
        Assert.False(await scope.ExistsAsync(Content(Data)));
    }

    [Fact]
    public async Task BlobStore_filesystem_rejects_expired_wrong_key_and_malformed_grants()
    {
        var clock = new BlobClock();
        await using var scope = await environment.CreateAsync(false, clock: clock);
        var store = (FileSystemBlobStore)scope.Store;
        var grant = store.CreateUploadGrant(Content(Data), Data.Length, TimeSpan.FromMinutes(1));
        clock.Advance(TimeSpan.FromMinutes(1));
        using var input = new MemoryStream(Data);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.UploadWithGrantAsync(grant, input, Token));
        foreach (var malformed in new[] { "", "..", "not-a-grant", new string('a', 2049) })
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.UploadWithGrantAsync(malformed, input, Token));
        await using var other = await environment.CreateAsync(false);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            ((FileSystemBlobStore)other.Store).UploadWithGrantAsync(grant, input, Token));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            store.CreateUploadGrant(Content(Data), Data.Length, TimeSpan.FromMinutes(16)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            store.CreateUploadGrant(Content(Data), Data.Length - 1, TimeSpan.FromMinutes(1)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BlobStore_filesystem_rejects_directory_and_final_file_links(bool directoryLink)
    {
        await using var scope = await environment.CreateAsync(false);
        var foreign = Path.Combine(scope.Root!, "foreign");
        Directory.CreateDirectory(foreign);
        var content = Content(Data);
        var target = Path.Combine(scope.Root!, content.Key.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            if (directoryLink) Directory.CreateSymbolicLink(Path.GetDirectoryName(target)!, foreign);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var foreignFile = Path.Combine(foreign, "untouched");
                await File.WriteAllBytesAsync(foreignFile, Data, Token);
                File.CreateSymbolicLink(target, foreignFile);
            }
        }
        catch (UnauthorizedAccessException)
        {
            Assert.Skip("Symbolic-link tests require Windows Developer Mode or SeCreateSymbolicLinkPrivilege.");
        }
        using var input = new MemoryStream(Data);
        await Assert.ThrowsAsync<IOException>(() => scope.Store.UploadAsync(content, input, Token));
        await Assert.ThrowsAsync<IOException>(() => scope.Store.DownloadAsync(content, Stream.Null, Token));
        Assert.DoesNotContain(Directory.GetFiles(foreign), file => Path.GetFileName(file) != "untouched");
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("C:\\escape")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/")]
    public void BlobStore_rejects_path_in_sha_and_undefined_kind(string sha)
    {
        Assert.Throws<ArgumentException>(() => new BlobContent(BlobKind.Artifacts, sha, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BlobContent((BlobKind)999, new string('a', 64), 1));
    }

    [Fact]
    public async Task BlobStore_Azure_SAS_is_create_only_scoped_to_staging_then_verified_before_publication()
    {
        await using var scope = await environment.CreateAsync(true);
        var store = (AzureBlobStore)scope.Store;
        var content = Content(Data);
        var grant = await store.CreateUploadGrantAsync(content, Data.Length, TimeSpan.FromMinutes(1), Token);
        Assert.Contains("sp=c", grant.UploadUri.Query);
        var anonymous = new BlobClient(grant.UploadUri, BlobStoreEnvironment.ClientOptions());
        using var input = new MemoryStream(Data);
        await anonymous.UploadAsync(input, cancellationToken: Token);
        Assert.False(await scope.ExistsAsync(content));
        var readError = await Assert.ThrowsAsync<RequestFailedException>(() =>
            anonymous.DownloadStreamingAsync(cancellationToken: Token));
        Assert.Equal(403, readError.Status);
        using var overwriteInput = new MemoryStream(Data);
        var overwriteError = await Assert.ThrowsAsync<RequestFailedException>(() =>
            anonymous.UploadAsync(overwriteInput, overwrite: true, cancellationToken: Token));
        Assert.Equal(403, overwriteError.Status);
        var otherPath = new UriBuilder(grant.UploadUri)
        {
            Path = $"{scope.ContainerUri!.AbsolutePath}/{content.Key}"
        };
        using var forbiddenInput = new MemoryStream(Data);
        var pathError = await Assert.ThrowsAsync<RequestFailedException>(() =>
            new BlobClient(otherPath.Uri, BlobStoreEnvironment.ClientOptions())
                .UploadAsync(forbiddenInput, cancellationToken: Token));
        Assert.Equal(403, pathError.Status);
        var deleteError = await Assert.ThrowsAsync<RequestFailedException>(() =>
            anonymous.DeleteAsync(cancellationToken: Token));
        Assert.Equal(403, deleteError.Status);
        await store.CompleteUploadAsync(grant, Token);
        await store.DownloadAsync(content, Stream.Null, Token);
        await scope.AssertNoStagingAsync();
    }

    [Fact]
    public async Task BlobStore_Azure_emulator_rejects_expired_SAS_at_direct_write_boundary()
    {
        var clock = new BlobClock();
        clock.Advance(TimeSpan.FromMinutes(-2));
        await using var scope = await environment.CreateAsync(true, clock: clock);
        var grant = await ((AzureBlobStore)scope.Store).CreateUploadGrantAsync(
            Content(Data), Data.Length, TimeSpan.FromMinutes(1), Token);
        using var input = new MemoryStream(Data);
        var error = await Assert.ThrowsAsync<RequestFailedException>(() =>
            new BlobClient(grant.UploadUri, BlobStoreEnvironment.ClientOptions()).UploadAsync(input, cancellationToken: Token));
        Assert.Equal(403, error.Status);
        Assert.False(await scope.ExistsAsync(Content(Data)));
        await scope.AssertNoStagingAsync();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task BlobStore_Azure_SAS_ignores_forged_sha_metadata_and_rejects_mismatch_or_size(int delta)
    {
        await using var scope = await environment.CreateAsync(true);
        var store = (AzureBlobStore)scope.Store;
        var content = Content(Data);
        var grant = await store.CreateUploadGrantAsync(content, Data.Length, TimeSpan.FromMinutes(1), Token);
        var bytes = delta switch { -1 => Data[..^1], 0 => new byte[Data.Length], _ => [.. Data, 42] };
        var anonymous = new BlobClient(grant.UploadUri, BlobStoreEnvironment.ClientOptions());
        using var input = new MemoryStream(bytes);
        await anonymous.UploadAsync(input, new BlobUploadOptions
        {
            Metadata = new Dictionary<string, string> { ["sha256"] = content.Sha256 }
        }, Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.CompleteUploadAsync(grant, Token));
        Assert.False(await scope.ExistsAsync(content));
        await scope.AssertNoStagingAsync();
    }

    [Fact]
    public async Task BlobStore_Azure_expired_or_foreign_grant_cannot_promote()
    {
        var clock = new BlobClock();
        await using var scope = await environment.CreateAsync(true, clock: clock);
        var store = (AzureBlobStore)scope.Store;
        var grant = await store.CreateUploadGrantAsync(Content(Data), Data.Length, TimeSpan.FromMinutes(1), Token);
        using var input = new MemoryStream(Data);
        await new BlobClient(grant.UploadUri, BlobStoreEnvironment.ClientOptions()).UploadAsync(input, cancellationToken: Token);
        await using var other = await environment.CreateAsync(true);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            ((AzureBlobStore)other.Store).CompleteUploadAsync(grant, Token));
        clock.Advance(TimeSpan.FromMinutes(1));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.CompleteUploadAsync(grant, Token));
        Assert.False(await scope.ExistsAsync(Content(Data)));
        await scope.AssertNoStagingAsync();
    }

    private static BlobContent Content(byte[] bytes) =>
        new(BlobKind.Artifacts, Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length);

    private sealed class PatternStream(long length, bool throwOnRead = false, Action? afterRead = null) : Stream
    {
        public long TotalLength => length;
        public long BytesRead { get; private set; }
        public int LargestRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (throwOnRead) throw new InvalidOperationException("Must not read oversized content.");
            LargestRead = Math.Max(LargestRead, buffer.Length);
            var count = (int)Math.Min(buffer.Length, length - BytesRead);
            buffer[..count].Fill(42);
            BytesRead += count;
            if (count > 0) afterRead?.Invoke();
            return count;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

public sealed class BlobClock : TimeProvider
{
    private DateTimeOffset now = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(TimeSpan duration) => now += duration;
}

public sealed class BlobStoreEnvironment : IAsyncLifetime
{
    private IContainer? azurite;
    private BlobServiceClient service = null!;
    private string emulatorConnectionString = "";

    public async ValueTask InitializeAsync()
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        azurite = new ContainerBuilder("mcr.microsoft.com/azure-storage/azurite:3.37.0")
            .WithName($"kardia-test-blob-store-azurite-{Guid.NewGuid().ToString("N")[..12]}")
            .WithEnvironment("AZURITE_ACCOUNTS", $"wfstore:{key}")
            .WithPortBinding(10000, assignRandomHostPort: true)
            .WithCommand("azurite-blob", "--blobHost", "0.0.0.0", "--skipApiVersionCheck",
                "--silent", "--inMemoryPersistence", "--disableTelemetry")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(10000))
            .Build();
        await azurite.StartAsync(TestContext.Current.CancellationToken);
        service = new BlobServiceClient(new Uri($"http://127.0.0.1:{azurite.GetMappedPublicPort(10000)}/wfstore"),
            new StorageSharedKeyCredential("wfstore", key), ClientOptions());
        emulatorConnectionString = $"DefaultEndpointsProtocol=http;AccountName=wfstore;AccountKey={key};BlobEndpoint={service.Uri};";
        await service.GetPropertiesAsync(TestContext.Current.CancellationToken);
    }

    public IBlobStore ConfiguredStore(string container) =>
        BlobStoreConfiguration.Create(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Storage:Provider"] = "AzureBlob",
                ["Storage:Azure:Container"] = container,
                ["Storage:Azure:EmulatorConnectionString"] = emulatorConnectionString
            }).Build(), RandomNumberGenerator.GetBytes(32), TimeProvider.System,
            WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Development" }).Environment);

    public async Task<BlobStoreScope> CreateAsync(bool azure,
        long maximumBytes = 32 * 1024 * 1024, TimeProvider? clock = null)
    {
        if (azure)
        {
            var container = service.GetBlobContainerClient($"blobtest-{Guid.NewGuid():N}");
            await container.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
            return new BlobStoreScope(new AzureBlobStore(service, container.Name, maximumBytes, clock), null, container);
        }
        var root = Path.GetFullPath($".blobstore-tests-{Guid.NewGuid():N}");
        return new BlobStoreScope(new FileSystemBlobStore(root, RandomNumberGenerator.GetBytes(32), maximumBytes, clock),
            root, null);
    }

    public static BlobClientOptions ClientOptions() => new()
    {
        Retry = { MaxRetries = 0, NetworkTimeout = TimeSpan.FromSeconds(30) },
        Diagnostics = { IsLoggingContentEnabled = false, IsLoggingEnabled = false }
    };

    public async ValueTask DisposeAsync()
    {
        if (azurite is not null) await azurite.DisposeAsync();
    }
}

public sealed class BlobStoreScope(IBlobStore store, string? root, BlobContainerClient? container) : IAsyncDisposable
{
    public IBlobStore Store => store;
    public string? Root => root;
    public Uri? ContainerUri => container?.Uri;
    public async Task<bool> ExistsAsync(BlobContent content) => container is null
        ? File.Exists(Path.Combine(root!, content.Key.Replace('/', Path.DirectorySeparatorChar)))
        : (await container.GetBlobClient(content.Key).ExistsAsync(TestContext.Current.CancellationToken)).Value;

    public async Task CorruptAsync(BlobContent content, byte[] bytes)
    {
        if (container is not null)
        {
            using var input = new MemoryStream(bytes);
            await container.GetBlobClient(content.Key).UploadAsync(input, overwrite: true,
                cancellationToken: TestContext.Current.CancellationToken);
        }
        else
        {
            var target = Path.Combine(root!, content.Key.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllBytesAsync(target, bytes, TestContext.Current.CancellationToken);
        }
    }

    public async Task AssertNoStagingAsync()
    {
        if (container is null) Assert.Empty(Directory.GetFiles(root!, ".upload-*"));
        else
        {
            await foreach (var _ in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix: ".staging/",
                cancellationToken: TestContext.Current.CancellationToken))
                Assert.Fail("Unpublished staging blob was not cleaned up.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (store is IDisposable disposable) disposable.Dispose();
        if (container is not null) await container.DeleteAsync();
        if (root is not null && Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
