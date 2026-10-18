using System.Security.Cryptography;
using System.Text;
using Wf.ControlPlane.Engine;
using Wf.ControlPlane.Storage;

namespace Wf.Integration.Tests.Storage;

public sealed class RetentionBlobStoreTests(BlobStoreEnvironment environment) : IClassFixture<BlobStoreEnvironment>
{
    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run_deletion_is_exact_idempotent_and_never_touches_other_runs_or_shared_content(bool azure)
    {
        await using var scope = await environment.CreateAsync(azure);
        var runs = new[] { Guid.CreateVersion7(), Guid.CreateVersion7() };
        var bytes = Encoding.UTF8.GetBytes("retention adapter bytes");
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var named = runs.SelectMany(run => new[]
        {
            new BlobContent(BlobKind.Logs, hash, bytes.Length, $"logs/{run:D}/{new string('a',64)}/1/000001.log.gz"),
            new BlobContent(BlobKind.Reports, hash, bytes.Length, $"reports/{run:D}/{Guid.CreateVersion7():D}"),
            new BlobContent(BlobKind.Resources, hash, bytes.Length, $"resources/{run:D}/{ResourceSampleSink.TaskHash("work/task")}/1/1-{hash}.json")
        }).ToArray();
        var shared = new[]
        {
            new BlobContent(BlobKind.Artifacts, hash, bytes.Length),
            new BlobContent(BlobKind.Packages, hash, bytes.Length),
            new BlobContent(BlobKind.Sources, hash, bytes.Length)
        };
        foreach (var content in named.Concat(shared))
        {
            using var input = new MemoryStream(bytes);
            await scope.Store.UploadAsync(content, input, Token);
        }
        var listed = new List<Guid>();
        await foreach (var run in scope.Store.RunObjectsAsync(Token)) listed.Add(run);
        Assert.Equal(runs.Order(), listed.Order());
        await scope.Store.DeleteRunObjectsAsync(runs[0], Token);
        await scope.Store.DeleteRunObjectsAsync(runs[0], Token);
        foreach (var content in named.Take(3)) Assert.False(await scope.ExistsAsync(content));
        foreach (var content in named.Skip(3).Concat(shared)) await scope.Store.DownloadAsync(content, Stream.Null, Token);
        var hashes = new List<string>();
        await foreach (var item in scope.Store.ArtifactHashesAsync(Token)) hashes.Add(item);
        Assert.Equal(hash, Assert.Single(hashes));
        await scope.Store.DeleteArtifactAsync(hash, Token);
        await scope.Store.DeleteArtifactAsync(hash, Token);
        Assert.False(await scope.ExistsAsync(shared[0]));
        foreach (var content in shared.Skip(1)) await scope.Store.DownloadAsync(content, Stream.Null, Token);
        await scope.AssertNoStagingAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Malformed_run_ids_hashes_and_traversal_are_rejected_before_storage_access(bool azure)
    {
        await using var scope = await environment.CreateAsync(azure);
        await Assert.ThrowsAsync<ArgumentException>(() => scope.Store.DeleteRunObjectsAsync(Guid.Empty, Token));
        await Assert.ThrowsAsync<ArgumentException>(() => scope.Store.DeleteRunObjectsAsync(Guid.NewGuid(), Token));
        foreach (var hash in new[] { "", "..", new string('A', 64), new string('g', 64), "../" + new string('a', 64) })
            await Assert.ThrowsAsync<ArgumentException>(() => scope.Store.DeleteArtifactAsync(hash, Token));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.Store.DeleteRunObjectsAsync(Guid.CreateVersion7(), cancel.Token));
    }

    [Fact]
    public async Task Filesystem_run_prefix_link_cannot_escape_into_a_retained_run()
    {
        await using var scope = await environment.CreateAsync(false);
        var keep = Guid.CreateVersion7();
        var remove = Guid.CreateVersion7();
        var directory = Path.Combine(scope.Root!, "logs", keep.ToString("D"));
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "retained.txt");
        await File.WriteAllTextAsync(file, "retained", Token);
        var link = Path.Combine(scope.Root!, "logs", remove.ToString("D"));
        Directory.CreateSymbolicLink(link, directory);
        try
        {
            await Assert.ThrowsAsync<IOException>(() => scope.Store.DeleteRunObjectsAsync(remove, Token));
            Assert.Equal("retained", await File.ReadAllTextAsync(file, Token));
        }
        finally { Directory.Delete(link); }
    }
}
