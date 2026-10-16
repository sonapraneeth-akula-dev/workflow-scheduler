using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Wf.Agent.Packages;

namespace Wf.Agent.Tests;

public sealed class PackageCacheTests
{
    internal static byte[] Zip(params (string Name, string Content)[] entries)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in entries)
            {
                var entry = zip.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                writer.Write(content);
            }
        return output.ToArray();
    }

    private static byte[] Valid() => Zip(("wf-package.json", """{"version":"1","entryAssembly":"workflow.dll"}"""),
        ("workflow.dll", "compiled IL"), ("workflow.runtimeconfig.json", "{}"));

    [Fact]
    public async Task Concurrent_same_sha_requests_download_once_and_warm_hit_rechecks_integrity()
    {
        var bytes = Valid();
        await using var fixture = new CacheFixture(bytes);
        var packages = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => fixture.GetAsync()));
        Assert.Equal(1, fixture.Handler.Count);
        Assert.All(packages, package =>
        {
            Assert.Equal("dotnet", package.Executable);
            Assert.Equal("1", package.Version);
            Assert.True(File.Exists(Assert.Single(package.Arguments)));
        });
        Assert.Single(Directory.GetDirectories(fixture.Root));
        Assert.Equal(packages[0].WorkingDirectory, packages[4].WorkingDirectory);
    }

    [Theory]
    [InlineData("../outside.dll")]
    [InlineData("/absolute.dll")]
    [InlineData("C:/absolute.dll")]
    [InlineData("..\\outside.dll")]
    [InlineData("folder/../../outside.dll")]
    [InlineData("workflow.dll:stream")]
    public async Task Traversal_absolute_and_ads_archive_paths_never_escape_staging(string unsafePath)
    {
        await using var fixture = new CacheFixture(Zip(
            ("wf-package.json", """{"version":"1","entryAssembly":"workflow.dll"}"""), ("workflow.dll", "IL"), (unsafePath, "unsafe")));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.GetAsync());
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Root));
    }

    [Fact]
    public async Task Case_colliding_archive_entries_are_rejected_on_every_os()
    {
        await using var fixture = new CacheFixture(Zip(
            ("wf-package.json", """{"version":"1","entryAssembly":"workflow.dll"}"""), ("workflow.dll", "IL"), ("WORKFLOW.dll", "other")));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.GetAsync());
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Root));
    }

    [Fact]
    public async Task Symbolic_link_archive_entries_are_rejected()
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            var link = archive.CreateEntry("link");
            link.ExternalAttributes = unchecked((int)0xA1FF0000);
            using var writer = new StreamWriter(link.Open());
            writer.Write("outside");
        }
        await using var fixture = new CacheFixture(memory.ToArray());
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.GetAsync());
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Root));
    }

    [Theory]
    [InlineData("""{"version":"wrong","entryAssembly":"workflow.dll"}""")]
    [InlineData("""{"version":"1","entryAssembly":"../workflow.dll"}""")]
    [InlineData("""{"version":"1","entryAssembly":"missing.dll"}""")]
    public async Task Manifest_must_match_authored_version_and_contained_entry_assembly(string manifest)
    {
        await using var fixture = new CacheFixture(Zip(("wf-package.json", manifest), ("workflow.dll", "IL")));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.GetAsync());
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Root));
    }

    [Fact]
    public async Task Download_sha_mismatch_is_not_published_as_a_cache_hit()
    {
        await using var fixture = new CacheFixture(Valid());
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Cache.GetAsync("1", CacheFixture.Url, new string('0', 64),
            TestContext.Current.CancellationToken));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Root));
    }

    [Fact]
    public async Task Cached_executable_corruption_fails_without_silent_redownload()
    {
        await using var fixture = new CacheFixture(Valid());
        var package = await fixture.GetAsync();
        await File.WriteAllTextAsync(Assert.Single(package.Arguments), "tampered", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.GetAsync());
        Assert.Equal(1, fixture.Handler.Count);
    }

    [Fact]
    public async Task Cached_archive_corruption_is_rejected_before_execution()
    {
        await using var fixture = new CacheFixture(Valid());
        var package = await fixture.GetAsync();
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(package.WorkingDirectory)!, "package.zip"),
            "tampered", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.GetAsync());
        Assert.Equal(1, fixture.Handler.Count);
    }

    [Fact]
    public async Task Extra_cached_files_cannot_be_injected_into_dependency_resolution()
    {
        await using var fixture = new CacheFixture(Valid());
        var package = await fixture.GetAsync();
        await File.WriteAllTextAsync(Path.Combine(package.WorkingDirectory, "injected.dll"), "tampered",
            TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.GetAsync());
    }

    [Theory]
    [InlineData("http://remote.example/package.zip")]
    [InlineData("file:///package.zip")]
    [InlineData("https://user:pass@example.invalid/package.zip")]
    [InlineData("https://example.invalid/package.zip#fragment")]
    public async Task Invalid_url_is_rejected_before_http(string uri)
    {
        await using var fixture = new CacheFixture(Valid());
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Cache.GetAsync("1", new Uri(uri), fixture.Sha,
            TestContext.Current.CancellationToken));
        Assert.Equal(0, fixture.Handler.Count);
    }

    [Theory]
    [InlineData("http://wf-api:8080/package.zip", true)]
    [InlineData("http://WF-API:8080/package.zip", true)]
    [InlineData("http://wf-api:8081/package.zip", false)]
    [InlineData("http://wf-api.example:8080/package.zip", false)]
    [InlineData("http://another-api:8080/package.zip", false)]
    public async Task Explicit_isolated_HTTP_origin_does_not_allow_other_hosts_or_ports(string uri, bool allowed)
    {
        await using var fixture = new CacheFixture(Valid());
        using var cache = new PackageCache(fixture.Root, NullLogger<PackageCache>.Instance,
            fixture.Handler, insecureOrigin: new("http://wf-api:8080"));
        if (allowed)
        {
            using var package = await cache.GetAsync("1", new(uri), fixture.Sha, TestContext.Current.CancellationToken);
            Assert.Equal(1, fixture.Handler.Count);
        }
        else
        {
            await Assert.ThrowsAsync<ArgumentException>(() =>
                cache.GetAsync("1", new(uri), fixture.Sha, TestContext.Current.CancellationToken));
            Assert.Equal(0, fixture.Handler.Count);
        }
    }

    [Theory]
    [InlineData("https://wf-api:8080")]
    [InlineData("http://wf-api:8080/path")]
    [InlineData("http://wf-api:8080/?query=value")]
    public void Insecure_package_origin_must_be_an_exact_HTTP_origin(string uri)
    {
        var root = Path.GetFullPath(Path.Combine(".agent-artifacts", "package-tests", Guid.NewGuid().ToString("N")));
        Assert.Throws<ArgumentException>(() =>
            new PackageCache(root, NullLogger<PackageCache>.Instance, insecureOrigin: new(uri)));
        Assert.False(Directory.Exists(root));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("../escape")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public async Task Invalid_sha_is_rejected_before_http(string sha)
    {
        await using var fixture = new CacheFixture(Valid());
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Cache.GetAsync("1", CacheFixture.Url, sha,
            TestContext.Current.CancellationToken));
        Assert.Equal(0, fixture.Handler.Count);
    }

    [Fact]
    public async Task Oversized_download_content_length_is_rejected_without_reading_body()
    {
        await using var fixture = new CacheFixture(Valid(), (_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
            response.Content.Headers.ContentLength = PackageCache.MaxArchiveBytes + 1;
            return Task.FromResult(response);
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.GetAsync());
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Root));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Redirect)]
    public async Task Http_failure_or_redirect_does_not_publish_a_cache_directory(HttpStatusCode status)
    {
        await using var fixture = new CacheFixture(Valid(), (_, _) => Task.FromResult(new HttpResponseMessage(status)));
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.GetAsync());
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Root));
    }

    [Fact]
    public async Task Cancellation_during_download_cleans_staging_without_affecting_existing_packages()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var began = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new CacheFixture(Valid(), async (_, token) =>
        {
            began.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("unreachable");
        });
        var download = fixture.Cache.GetAsync("1", CacheFixture.Url, fixture.Sha, cancellation.Token);
        await began.Task.WaitAsync(TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Root));
    }

    internal sealed class CacheFixture : IAsyncDisposable
    {
        internal static readonly Uri Url = new("http://localhost/package.zip");
        internal string Root { get; } = Path.GetFullPath(Path.Combine(".agent-artifacts", "package-tests", Guid.NewGuid().ToString("N")));
        internal string Sha { get; }
        internal CountingHandler Handler { get; }
        internal PackageCache Cache { get; }

        internal CacheFixture(byte[] bytes, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? response = null)
        {
            Sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
            Handler = new CountingHandler(response ?? ((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new ByteArrayContent(bytes) })));
            Cache = new PackageCache(Root, NullLogger<PackageCache>.Instance, Handler);
        }

        internal Task<Wf.Agent.Hosts.TaskHostPackage> GetAsync() => Cache.GetAsync("1", Url, Sha, TestContext.Current.CancellationToken);

        public ValueTask DisposeAsync()
        {
            Cache.Dispose();
            Directory.Delete(Root, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    internal sealed class CountingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        private int count;
        internal int Count => Volatile.Read(ref count);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref count);
            return response(request, cancellationToken);
        }
    }
}
