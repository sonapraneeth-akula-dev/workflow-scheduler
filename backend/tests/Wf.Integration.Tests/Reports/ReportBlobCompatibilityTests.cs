using System.Security.Cryptography;
using Wf.ControlPlane.Storage;
using Wf.Integration.Tests.Storage;

namespace Wf.Integration.Tests.Reports;

public sealed class ReportBlobCompatibilityTests(BlobStoreEnvironment environment) : IClassFixture<BlobStoreEnvironment>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReportPublish_named_report_keys_round_trip_through_filesystem_and_real_Azure_emulator(bool azure)
    {
        await using var scope = await environment.CreateAsync(azure);
        var bytes = new byte[] { 1, 2, 3 };
        var key = $"reports/{Guid.CreateVersion7():D}/{Guid.CreateVersion7():D}";
        var content = new BlobContent(BlobKind.Reports, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length, key);
        using var grants = new BlobUploadGrants(RandomNumberGenerator.GetBytes(32));
        var restored = grants.Read(grants.Create(content, content.SizeBytes, TimeSpan.FromMinutes(1)));
        Assert.Equal(key, restored.Key);
        await scope.Store.UploadAsync(restored, new MemoryStream(bytes), TestContext.Current.CancellationToken);
        await scope.Store.UploadAsync(restored, new MemoryStream(bytes), TestContext.Current.CancellationToken);
        using var output = new MemoryStream();
        await scope.Store.DownloadAsync(restored, output, TestContext.Current.CancellationToken);
        Assert.Equal(bytes, output.ToArray());
        var changed = new byte[] { 4, 5, 6 };
        await Assert.ThrowsAsync<InvalidDataException>(() => scope.Store.UploadAsync(
            new(BlobKind.Reports, Convert.ToHexStringLower(SHA256.HashData(changed)), changed.Length, key),
            new MemoryStream(changed), TestContext.Current.CancellationToken));
        await scope.AssertNoStagingAsync();
    }

    [Theory]
    [InlineData("../report")]
    [InlineData("reports/a/b")]
    [InlineData("reports/00000000-0000-0000-0000-000000000000/00000000-0000-0000-0000-000000000000")]
    [InlineData("reports/01990000-0000-7000-8000-000000000000/01990000-0000-7000-8000-000000000000/extra")]
    [InlineData("logs/01990000-0000-7000-8000-000000000000/01990000-0000-7000-8000-000000000000")]
    public void ReportPublish_named_report_keys_reject_aliases_or_path_traversal(string key) =>
        Assert.Throws<ArgumentException>(() => new BlobContent(BlobKind.Reports, new string('a', 64), 0, key));
}
