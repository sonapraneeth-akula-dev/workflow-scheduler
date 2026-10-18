using System.Security.Cryptography;
using System.Text;
using Wf.ControlPlane.Engine;
using Wf.ControlPlane.Storage;
using Wf.Integration.Tests.Storage;

namespace Wf.Integration.Tests.Resources;

public sealed class ResourceSeriesBlobStoreTests(BlobStoreEnvironment environment) : IClassFixture<BlobStoreEnvironment>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResourceSeries_named_chunks_round_trip_and_retention_deletes_only_the_selected_run(bool azure)
    {
        var token = TestContext.Current.CancellationToken;
        await using var scope = await environment.CreateAsync(azure);
        var runs = new[] { Guid.CreateVersion7(), Guid.CreateVersion7() };
        var bytes = Encoding.UTF8.GetBytes("resource storage adapter integrity fixture");
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var contents = runs.Select(run => new BlobContent(BlobKind.Resources, sha, bytes.Length,
            $"resources/{run:D}/{ResourceSampleSink.TaskHash("work/resources")}/1/1-{sha}.json")).ToArray();
        foreach (var content in contents)
            await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
            {
                using var source = new MemoryStream(bytes, writable: false);
                await scope.Store.UploadAsync(content, source, token);
            }));
        var artifact = new BlobContent(BlobKind.Artifacts, sha, bytes.Length);
        using (var source = new MemoryStream(bytes, writable: false))
            await scope.Store.UploadAsync(artifact, source, token);
        var listed = new List<Guid>();
        await foreach (var run in scope.Store.ResourceRunsAsync(token)) listed.Add(run);
        Assert.Equal(runs.Order(), listed.Order());
        await scope.Store.DeleteResourceRunAsync(runs[0], token);
        await scope.Store.DeleteResourceRunAsync(runs[0], token);
        Assert.False(await scope.ExistsAsync(contents[0]));
        Assert.True(await scope.ExistsAsync(contents[1]));
        Assert.True(await scope.ExistsAsync(artifact));
        using var destination = new MemoryStream();
        await scope.Store.DownloadAsync(contents[1], destination, token);
        Assert.Equal(bytes, destination.ToArray());
        Assert.Throws<ArgumentException>(() => new BlobContent(BlobKind.Resources, sha, bytes.Length,
            $"resources/{runs[0]:D}/../1/1-{sha}.json"));
        await scope.AssertNoStagingAsync();
    }
}
