using System.Security.Cryptography;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Logs;
using Wf.ControlPlane.Storage;
using Wf.Integration.Tests.Storage;

namespace Wf.Integration.Tests.Logs;

public sealed class TaskLogsBlobReadTests(BlobStoreEnvironment environment) : IClassFixture<BlobStoreEnvironment>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ephemeral_cleanup_deletes_only_canonical_live_keys_and_preserves_archived_index_and_chunks(bool azure)
    {
        var token = TestContext.Current.CancellationToken;
        await using var scope = await environment.CreateAsync(azure);
        var attempt = new AttemptRef { RunId = Guid.CreateVersion7().ToString(), NodeKey = "node", TaskKey = "task", Attempt = 1 };
        var prefix = LogIndex.PrefixFor(attempt);
        byte[] bytes = [1, 2, 3];
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var live = prefix + "live-" + sha + ".log.gz";
        var index = prefix + "index.json";
        var chunk = prefix + "000001.log.gz";
        foreach (var key in new[] { live, index, chunk })
            await scope.Store.UploadAsync(new(BlobKind.Logs, sha, bytes.Length, key), new MemoryStream(bytes), token);
        foreach (var invalid in new[] { null!, "", "../" + live, live + "?sig=secret", prefix + "live-bad.log.gz", index, chunk })
            await Assert.ThrowsAsync<ArgumentException>(() => scope.Store.DeleteLiveLogAsync(invalid, token));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.Store.DeleteLiveLogAsync(live, canceled.Token));
        await using (var remaining = await scope.Store.OpenLogReadAsync(live, bytes.Length, token))
            Assert.Equal(bytes.Length, remaining.Length);
        await scope.Store.DeleteLiveLogAsync(live, token);
        await scope.Store.DeleteLiveLogAsync(live, token);
        await Assert.ThrowsAsync<FileNotFoundException>(() => scope.Store.OpenLogReadAsync(live, bytes.Length, token));
        foreach (var key in new[] { index, chunk })
        {
            await using var input = await scope.Store.OpenLogReadAsync(key, bytes.Length, token);
            using var output = new MemoryStream();
            await input.CopyToAsync(output, token);
            Assert.Equal(bytes, output.ToArray());
        }
        await scope.AssertNoStagingAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TaskLogs_named_index_read_is_bounded_private_and_disposable_for_filesystem_and_actual_Azure(bool azure)
    {
        var token = TestContext.Current.CancellationToken;
        await using var scope = await environment.CreateAsync(azure);
        var attempt = new AttemptRef { RunId = Guid.CreateVersion7().ToString(), NodeKey = "node", TaskKey = "task", Attempt = 1 };
        var key = LogIndex.PrefixFor(attempt) + "index.json";
        var bytes = new byte[300_000];
        RandomNumberGenerator.Fill(bytes);
        await scope.Store.UploadAsync(new(BlobKind.Logs, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length, key),
            new MemoryStream(bytes), token);
        await Assert.ThrowsAsync<InvalidDataException>(() => scope.Store.OpenLogReadAsync(key, bytes.Length - 1, token));
        await using (var input = await scope.Store.OpenLogReadAsync(key, bytes.Length, token))
        {
            var buffer = new byte[257];
            await input.ReadExactlyAsync(buffer, token);
            Assert.Equal(bytes[..257], buffer);
            // Dispose without draining: no full-object integrity read is required for immutable index discovery.
        }
        await using (var input = await scope.Store.OpenLogReadAsync(key, bytes.Length, token))
        {
            using var output = new MemoryStream();
            await input.CopyToAsync(output, token);
            Assert.Equal(bytes, output.ToArray());
        }
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            scope.Store.OpenLogReadAsync(LogIndex.PrefixFor(attempt) + "000099.log.gz", 100, token));
        foreach (var invalid in new[] { "../index.json", key + "?sig=secret", key + "/other", key.Replace("index.json", "..") })
            await Assert.ThrowsAsync<ArgumentException>(() => scope.Store.OpenLogReadAsync(invalid, bytes.Length, token));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.Store.OpenLogReadAsync(key, bytes.Length, canceled.Token));
        await scope.AssertNoStagingAsync();
    }
}
