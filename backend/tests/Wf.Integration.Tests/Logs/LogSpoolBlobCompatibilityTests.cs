using System.Security.Cryptography;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Logs;
using Wf.ControlPlane.Storage;
using Wf.Integration.Tests.Storage;

namespace Wf.Integration.Tests.Logs;

public sealed class LogSpoolBlobCompatibilityTests(BlobStoreEnvironment environment) : IClassFixture<BlobStoreEnvironment>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Named_attempt_log_objects_work_with_filesystem_and_real_Azure_emulator_without_overwrite(bool azure)
    {
        await using var scope = await environment.CreateAsync(azure);
        var bytes = new byte[] { 1, 2, 3 };
        var attempt = new AttemptRef { RunId = Guid.NewGuid().ToString(), NodeKey = "work", TaskKey = "logs", Attempt = 1 };
        var key = LogIndex.PrefixFor(attempt) + "000000.log.gz";
        var content = new BlobContent(BlobKind.Logs, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length, key);
        await scope.Store.UploadAsync(content, new MemoryStream(bytes), TestContext.Current.CancellationToken);
        await scope.Store.UploadAsync(content, new MemoryStream(bytes), TestContext.Current.CancellationToken);
        using var output = new MemoryStream();
        await scope.Store.DownloadAsync(content, output, TestContext.Current.CancellationToken);
        Assert.Equal(bytes, output.ToArray());
        var replacement = new byte[] { 4, 5, 6 };
        var changed = new BlobContent(BlobKind.Logs, Convert.ToHexStringLower(SHA256.HashData(replacement)), replacement.Length, key);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            scope.Store.UploadAsync(changed, new MemoryStream(replacement), TestContext.Current.CancellationToken));
        await scope.Store.DownloadAsync(content, Stream.Null, TestContext.Current.CancellationToken);
        await scope.AssertNoStagingAsync();
    }
}
