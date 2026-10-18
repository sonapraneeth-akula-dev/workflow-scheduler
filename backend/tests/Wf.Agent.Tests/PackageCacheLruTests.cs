using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Wf.Agent.Hosts;
using Wf.Agent.Packages;

namespace Wf.Agent.Tests;

public sealed class PackageCacheLruTests
{
    [Fact]
    public async Task Recovery_restores_all_verified_pins_before_enforcing_a_reduced_cache_budget()
    {
        var a = Archive("aaa");
        var b = Archive("bbb");
        var c = Archive("ccc");
        using var fixture = new BoundedCacheFixture([a, b, c], maxPackages: 2);
        using var recordedA = await fixture.GetAsync(a);
        using var recordedB = await fixture.GetAsync(b);
        recordedA.Dispose();
        recordedB.Dispose();
        fixture.Restart(maxPackages: 1);
        using var restoredA = await fixture.Cache.RestoreExistingAsync(recordedA, TestContext.Current.CancellationToken);
        using var restoredB = await fixture.Cache.RestoreExistingAsync(recordedB, TestContext.Current.CancellationToken);
        Assert.True(File.Exists(Assert.Single(restoredA.Arguments)));
        Assert.True(File.Exists(Assert.Single(restoredB.Arguments)));
        Assert.Equal(1, fixture.Downloads[Hash(a)]);
        Assert.Equal(1, fixture.Downloads[Hash(b)]);
        await Assert.ThrowsAsync<IOException>(() => fixture.GetAsync(c));
        restoredA.Dispose();
        restoredB.Dispose();
        (await fixture.GetAsync(c)).Dispose();
        Assert.False(Directory.Exists(fixture.PathFor(a)));
        Assert.False(Directory.Exists(fixture.PathFor(b)));
    }

    [Fact]
    public async Task Recovery_rejects_a_runtime_outside_the_cache_or_different_from_the_verified_package()
    {
        var bytes = Archive("aaa");
        using var fixture = new BoundedCacheFixture([bytes]);
        using var recorded = await fixture.GetAsync(bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Cache.RestoreExistingAsync(
            new(recorded.Version, recorded.Executable, ["foreign.dll"], recorded.WorkingDirectory),
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Cache.RestoreExistingAsync(
            new(recorded.Version, recorded.Executable, recorded.Arguments, Path.GetTempPath()),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Warm_touch_changes_lru_order_and_evicted_content_is_downloaded_again()
    {
        var a = Archive("aaa");
        var b = Archive("bbb");
        var c = Archive("ccc");
        using var fixture = new BoundedCacheFixture([a, b, c], maxPackages: 2);
        (await fixture.GetAsync(a)).Dispose();
        (await fixture.GetAsync(b)).Dispose();
        var firstTouch = Directory.GetLastWriteTimeUtc(fixture.PathFor(a));
        (await fixture.GetAsync(a)).Dispose();
        Assert.True(Directory.GetLastWriteTimeUtc(fixture.PathFor(a)) > firstTouch);
        (await fixture.GetAsync(c)).Dispose();
        Assert.True(Directory.Exists(fixture.PathFor(a)));
        Assert.False(Directory.Exists(fixture.PathFor(b)));
        Assert.True(Directory.Exists(fixture.PathFor(c)));
        (await fixture.GetAsync(b)).Dispose();
        Assert.False(Directory.Exists(fixture.PathFor(a)));
        Assert.Equal(2, fixture.Downloads[Hash(b)]);
        Assert.Equal(2, Directory.GetDirectories(fixture.Root).Length);
    }

    [Fact]
    public async Task Byte_budget_counts_archive_and_extracted_dependencies_not_only_package_count()
    {
        var a = Archive("aaa");
        var b = Archive("bbb");
        var c = Archive("ccc");
        var budget = StoredBytes(a) + StoredBytes(b);
        using var fixture = new BoundedCacheFixture([a, b, c], maxCacheBytes: budget, maxPackages: 10);
        (await fixture.GetAsync(a)).Dispose();
        (await fixture.GetAsync(b)).Dispose();
        (await fixture.GetAsync(c)).Dispose();
        Assert.False(Directory.Exists(fixture.PathFor(a)));
        Assert.True(Directory.Exists(fixture.PathFor(b)));
        Assert.True(Directory.Exists(fixture.PathFor(c)));
        Assert.True(Directory.EnumerateFiles(fixture.Root, "*", SearchOption.AllDirectories)
            .Sum(path => new FileInfo(path).Length) <= budget);
    }

    [Fact]
    public async Task Restart_uses_persisted_warm_order_not_access_times_or_foreign_journals()
    {
        var a = Archive("aaa");
        var b = Archive("bbb");
        var c = Archive("ccc");
        using var fixture = new BoundedCacheFixture([a, b, c], maxPackages: 2);
        (await fixture.GetAsync(a)).Dispose();
        (await fixture.GetAsync(b)).Dispose();
        (await fixture.GetAsync(a)).Dispose();
        File.WriteAllText(Path.Combine(fixture.Root, "accessTimes"), $$"""{"{{Hash(a)}}":0,"{{Hash(b)}}":9999999999999}""");
        Directory.SetLastAccessTimeUtc(fixture.PathFor(b), DateTime.UtcNow.AddYears(5));
        fixture.Restart();
        (await fixture.GetAsync(c)).Dispose();
        Assert.True(Directory.Exists(fixture.PathFor(a)));
        Assert.False(Directory.Exists(fixture.PathFor(b)));
        Assert.True(File.Exists(Path.Combine(fixture.Root, "accessTimes")));
        Assert.Equal(1, fixture.Downloads[Hash(a)]);
    }

    [Fact]
    public async Task Older_pinned_package_survives_eviction_until_its_last_lease_is_disposed()
    {
        var a = Archive("aaa");
        var b = Archive("bbb");
        var c = Archive("ccc");
        using var fixture = new BoundedCacheFixture([a, b, c], maxPackages: 2);
        using var pin = await fixture.GetAsync(a);
        using var secondPin = await fixture.GetAsync(a);
        pin.Dispose();
        pin.Dispose();
        (await fixture.GetAsync(b)).Dispose();
        (await fixture.GetAsync(c)).Dispose();
        Assert.True(File.Exists(Assert.Single(secondPin.Arguments)));
        Assert.False(Directory.Exists(fixture.PathFor(b)));
        secondPin.Dispose();
        (await fixture.GetAsync(b)).Dispose();
        Assert.False(Directory.Exists(fixture.PathFor(a)));
    }

    [Fact]
    public async Task All_pinned_fails_explicitly_without_publishing_then_succeeds_after_release()
    {
        var a = Archive("aaa");
        var b = Archive("bbb");
        using var fixture = new BoundedCacheFixture([a, b], maxPackages: 1);
        using var pin = await fixture.GetAsync(a);
        var error = await Assert.ThrowsAsync<IOException>(() => fixture.GetAsync(b));
        Assert.Contains("pinned", error.Message);
        Assert.True(File.Exists(Assert.Single(pin.Arguments)));
        Assert.False(Directory.Exists(fixture.PathFor(b)));
        Assert.Single(Directory.EnumerateFileSystemEntries(fixture.Root));
        pin.Dispose();
        (await fixture.GetAsync(b)).Dispose();
        Assert.False(Directory.Exists(fixture.PathFor(a)));
        Assert.Equal(2, fixture.Downloads[Hash(b)]);
    }

    [Fact]
    public async Task Impossible_reservation_does_not_partially_evict_unpinned_entries()
    {
        var a = Archive("aaa");
        var b = Archive("bbb");
        var c = Archive(new string('c', 100));
        using var fixture = new BoundedCacheFixture([a, b, c], maxCacheBytes: StoredBytes(a) + StoredBytes(b));
        using var pin = await fixture.GetAsync(a);
        (await fixture.GetAsync(b)).Dispose();
        await Assert.ThrowsAsync<IOException>(() => fixture.GetAsync(c));
        Assert.True(Directory.Exists(fixture.PathFor(a)));
        Assert.True(Directory.Exists(fixture.PathFor(b)));
        Assert.False(Directory.Exists(fixture.PathFor(c)));
    }

    [Fact]
    public async Task Oversized_package_is_rejected_before_eviction_and_is_not_published()
    {
        var a = Archive("aaa");
        var b = Archive(new string('b', 5000));
        using var fixture = new BoundedCacheFixture([a, b], maxCacheBytes: StoredBytes(a));
        (await fixture.GetAsync(a)).Dispose();
        var error = await Assert.ThrowsAsync<IOException>(() => fixture.GetAsync(b));
        Assert.Contains("byte budget", error.Message);
        Assert.True(Directory.Exists(fixture.PathFor(a)));
        Assert.False(Directory.Exists(fixture.PathFor(b)));
        Assert.Single(Directory.EnumerateFileSystemEntries(fixture.Root));
    }

    [Fact]
    public async Task First_warm_request_after_restart_enforces_a_smaller_budget()
    {
        var a = Archive("aaa");
        var b = Archive("bbb");
        using var fixture = new BoundedCacheFixture([a, b], maxPackages: 2);
        (await fixture.GetAsync(a)).Dispose();
        (await fixture.GetAsync(b)).Dispose();
        fixture.Restart(maxPackages: 1);
        (await fixture.GetAsync(a)).Dispose();
        Assert.True(Directory.Exists(fixture.PathFor(a)));
        Assert.False(Directory.Exists(fixture.PathFor(b)));
        Assert.Equal(1, fixture.Downloads[Hash(a)]);
        fixture.Restart(maxCacheBytes: StoredBytes(a) - 1);
        await Assert.ThrowsAsync<IOException>(() => fixture.GetAsync(a));
        Assert.True(Directory.Exists(fixture.PathFor(a)));
    }

    [Fact]
    public async Task Concurrent_distinct_hash_leases_block_eviction_and_warm_hits_download_once()
    {
        var a = Archive("aaa");
        var b = Archive("bbb");
        var c = Archive("ccc");
        using var fixture = new BoundedCacheFixture([a, b, c], maxPackages: 2);
        var pins = await Task.WhenAll(Enumerable.Range(0, 20).Select(index => fixture.GetAsync(index % 2 == 0 ? a : b)));
        try
        {
            Assert.Equal(1, fixture.Downloads[Hash(a)]);
            Assert.Equal(1, fixture.Downloads[Hash(b)]);
            await Assert.ThrowsAsync<IOException>(() => fixture.GetAsync(c));
            Assert.All(pins, package => Assert.True(File.Exists(Assert.Single(package.Arguments))));
        }
        finally { foreach (var pin in pins) pin.Dispose(); }
        (await fixture.GetAsync(c)).Dispose();
        Assert.Equal(2, Directory.GetDirectories(fixture.Root).Length);
    }

    [Fact]
    public async Task Queued_requests_cannot_evict_a_just_published_package_before_its_caller_binds_a_host()
    {
        var a = Archive("aaa");
        var b = Archive("bbb");
        using var fixture = new BoundedCacheFixture([a, b], maxPackages: 1);
        (await fixture.GetAsync(a)).Dispose();
        var began = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Response = async (_, token) =>
        {
            began.SetResult();
            await finish.Task.WaitAsync(token);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(b) };
        };
        var downloading = fixture.GetAsync(b);
        await began.Task.WaitAsync(TestContext.Current.CancellationToken);
        fixture.Response = null;
        var queuedOtherHash = fixture.GetAsync(a);
        var queuedSameHash = fixture.GetAsync(b);
        finish.SetResult();
        using var firstPin = await downloading;
        await Assert.ThrowsAsync<IOException>(() => queuedOtherHash);
        using var secondPin = await queuedSameHash;
        firstPin.Dispose();
        await Assert.ThrowsAsync<IOException>(() => fixture.GetAsync(a));
        Assert.True(File.Exists(Assert.Single(secondPin.Arguments)));
        Assert.Equal(1, fixture.Downloads[Hash(b)]);
        secondPin.Dispose();
        (await fixture.GetAsync(a)).Dispose();
        Assert.False(Directory.Exists(fixture.PathFor(b)));
        Assert.Single(Directory.EnumerateFileSystemEntries(fixture.Root));
    }

    [Fact]
    public async Task Failed_or_cancelled_download_and_cancelled_waiter_preserve_existing_content()
    {
        var a = Archive("aaa");
        var b = Archive("bbb");
        using var fixture = new BoundedCacheFixture([a, b], maxPackages: 1);
        (await fixture.GetAsync(a)).Dispose();
        fixture.Response = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.GetAsync(b));
        var began = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Response = async (_, token) =>
        {
            began.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("unreachable");
        };
        using var downloadCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var download = fixture.GetAsync(b, downloadCancellation.Token);
        await began.Task.WaitAsync(TestContext.Current.CancellationToken);
        using var waiterCancellation = new CancellationTokenSource();
        var waiter = fixture.GetAsync(a, waiterCancellation.Token);
        await waiterCancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        await downloadCancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
        Assert.True(Directory.Exists(fixture.PathFor(a)));
        Assert.Single(Directory.EnumerateFileSystemEntries(fixture.Root));
        fixture.Response = null;
        (await fixture.GetAsync(b)).Dispose();
        Assert.False(Directory.Exists(fixture.PathFor(a)));
    }

    [Fact]
    public async Task Corrupt_incoming_or_warm_packages_are_not_silently_replaced_or_evicted()
    {
        var a = Archive("aaa");
        var b = Archive("bbb");
        using var fixture = new BoundedCacheFixture([a, b], maxPackages: 1);
        using (var package = await fixture.GetAsync(a))
            File.WriteAllText(Assert.Single(package.Arguments), "corrupt");
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.GetAsync(a));
        fixture.Response = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(a)
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.GetAsync(b));
        Assert.True(Directory.Exists(fixture.PathFor(a)));
        Assert.False(Directory.Exists(fixture.PathFor(b)));
        Assert.Equal(1, fixture.Downloads[Hash(a)]);
    }

    [Fact]
    public async Task Foreign_and_staging_directories_are_never_evicted()
    {
        var a = Archive("aaa");
        var b = Archive("bbb");
        using var fixture = new BoundedCacheFixture([a, b], maxPackages: 1);
        var foreign = Directory.CreateDirectory(Path.Combine(fixture.Root, "foreign")).FullName;
        var staging = Directory.CreateDirectory(Path.Combine(fixture.Root, $".{Hash(a)}.staging")).FullName;
        File.WriteAllText(Path.Combine(foreign, "keep"), "foreign");
        File.WriteAllText(Path.Combine(staging, "keep"), "staging");
        (await fixture.GetAsync(a)).Dispose();
        (await fixture.GetAsync(b)).Dispose();
        Assert.True(File.Exists(Path.Combine(foreign, "keep")));
        Assert.True(File.Exists(Path.Combine(staging, "keep")));
        Assert.False(Directory.Exists(fixture.PathFor(a)));
    }

    [Fact]
    public async Task Malformed_hash_named_directory_fails_closed_without_deleting_it()
    {
        var a = Archive("aaa");
        using var fixture = new BoundedCacheFixture([a]);
        var foreign = Directory.CreateDirectory(Path.Combine(fixture.Root, new string('f', 64))).FullName;
        File.WriteAllText(Path.Combine(foreign, "keep"), "foreign");
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.GetAsync(a));
        Assert.True(File.Exists(Path.Combine(foreign, "keep")));
        Assert.Empty(fixture.Downloads);
    }

    [Fact]
    public async Task Hash_named_directory_with_unverified_archive_is_not_deleted_as_owned_content()
    {
        var a = Archive("aaa");
        var b = Archive("bbb");
        using var fixture = new BoundedCacheFixture([a, b], maxPackages: 1);
        (await fixture.GetAsync(a)).Dispose();
        File.WriteAllText(Path.Combine(fixture.PathFor(a), "package.zip"), "foreign or corrupt");
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.GetAsync(b));
        Assert.Equal("foreign or corrupt", File.ReadAllText(Path.Combine(fixture.PathFor(a), "package.zip")));
        Assert.False(Directory.Exists(fixture.PathFor(b)));
    }

    [Fact]
    public async Task Windows_case_renamed_hash_directory_cannot_bypass_the_byte_budget()
    {
        if (!OperatingSystem.IsWindows()) return;
        var a = Archive("aaa");
        using var fixture = new BoundedCacheFixture([a]);
        (await fixture.GetAsync(a)).Dispose();
        var intermediate = Path.Combine(fixture.Root, "renaming");
        Directory.Move(fixture.PathFor(a), intermediate);
        Directory.Move(intermediate, Path.Combine(fixture.Root, Hash(a).ToUpperInvariant()));
        fixture.Restart(maxCacheBytes: StoredBytes(a) - 1);
        await Assert.ThrowsAsync<IOException>(() => fixture.GetAsync(a));
        Assert.Equal(1, fixture.Downloads[Hash(a)]);
    }

    [Fact]
    public async Task Symlinked_hash_entry_is_rejected_without_traversing_or_deleting_its_target()
    {
        var a = Archive("aaa");
        var b = Archive("bbb");
        using var fixture = new BoundedCacheFixture([a, b], maxPackages: 1);
        var foreign = Directory.CreateDirectory(Path.Combine(fixture.Root, "foreign")).FullName;
        File.WriteAllText(Path.Combine(foreign, "keep"), "foreign");
        var link = fixture.PathFor(a);
        try
        {
            try { Directory.CreateSymbolicLink(link, foreign); }
            catch (UnauthorizedAccessException) { Assert.Skip("Creating symbolic links requires an OS privilege."); }
            await Assert.ThrowsAsync<InvalidDataException>(() => fixture.GetAsync(b));
            Assert.Equal("foreign", File.ReadAllText(Path.Combine(foreign, "keep")));
            Assert.Empty(fixture.Downloads);
        }
        finally { if (Directory.Exists(link)) Directory.Delete(link); }
    }

    [Fact]
    public async Task Symlinked_root_or_ancestor_is_rejected_before_cache_creation()
    {
        using var fixture = new BoundedCacheFixture([Archive("aaa")]);
        var link = Path.Combine(fixture.Root, "root-link");
        var foreign = Directory.CreateDirectory(Path.Combine(fixture.Root, "foreign")).FullName;
        try
        {
            try { Directory.CreateSymbolicLink(link, foreign); }
            catch (UnauthorizedAccessException) { Assert.Skip("Creating symbolic links requires an OS privilege."); }
            Assert.Throws<InvalidDataException>(() => new PackageCache(link, NullLogger<PackageCache>.Instance));
            Assert.Throws<InvalidDataException>(() => new PackageCache(Path.Combine(link, "child"), NullLogger<PackageCache>.Instance));
            Assert.Empty(Directory.EnumerateFileSystemEntries(foreign));
        }
        finally { if (Directory.Exists(link)) Directory.Delete(link); }
    }

    [Fact]
    public async Task Windows_eviction_file_lock_is_reported_not_treated_as_reclaimed_space()
    {
        if (!OperatingSystem.IsWindows()) return;
        var a = Archive("aaa");
        var b = Archive("bbb");
        using var fixture = new BoundedCacheFixture([a, b], maxPackages: 1);
        (await fixture.GetAsync(a)).Dispose();
        using var locked = new FileStream(Path.Combine(fixture.PathFor(a), "package.zip"), FileMode.Open, FileAccess.Read, FileShare.Read);
        await Assert.ThrowsAnyAsync<IOException>(() => fixture.GetAsync(b));
        Assert.True(File.Exists(locked.Name));
        Assert.False(Directory.Exists(fixture.PathFor(b)));
        Assert.DoesNotContain(Directory.EnumerateDirectories(fixture.Root), path => Path.GetFileName(path).StartsWith('.'));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(1, 0)]
    [InlineData(1, -1)]
    public void Nonpositive_budgets_are_rejected(long bytes, int count)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PackageCache(Path.GetFullPath(".agent-artifacts"),
            NullLogger<PackageCache>.Instance, maxCacheBytes: bytes, maxPackages: count));
    }

    internal static byte[] Archive(string content) => PackageCacheTests.Zip(
        ("wf-package.json", """{"version":"1","entryAssembly":"workflow.dll"}"""),
        ("workflow.dll", content), ("dependencies/lazy.dll", content));

    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    internal static long StoredBytes(byte[] bytes)
    {
        using var memory = new MemoryStream(bytes);
        using var archive = new ZipArchive(memory, ZipArchiveMode.Read);
        return bytes.LongLength + archive.Entries.Sum(entry => entry.Length);
    }

    internal sealed class BoundedCacheFixture : IDisposable
    {
        private readonly Dictionary<string, byte[]> packages;
        private long maxCacheBytes;
        private int maxPackages;
        internal string Root { get; } = Path.GetFullPath(Path.Combine(".agent-artifacts", "package-lru-tests", Guid.NewGuid().ToString("N")));
        internal ConcurrentDictionary<string, int> Downloads { get; } = new(StringComparer.Ordinal);
        internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Response { get; set; }
        internal PackageCache Cache { get; private set; }

        internal BoundedCacheFixture(IEnumerable<byte[]> packages, long maxCacheBytes = PackageCache.DefaultMaxCacheBytes,
            int maxPackages = PackageCache.DefaultMaxPackages)
        {
            this.packages = packages.ToDictionary(Hash);
            this.maxCacheBytes = maxCacheBytes;
            this.maxPackages = maxPackages;
            Cache = CreateCache();
        }

        private PackageCache CreateCache() => new(Root, NullLogger<PackageCache>.Instance,
            new PackageCacheTests.CountingHandler((request, token) =>
            {
                var hash = request.RequestUri!.AbsolutePath.Trim('/');
                Downloads.AddOrUpdate(hash, 1, (_, count) => count + 1);
                return Response?.Invoke(request, token) ?? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(packages[hash])
                });
            }), maxCacheBytes, maxPackages, new FixedTimeProvider());

        internal Task<TaskHostPackage> GetAsync(byte[] bytes, CancellationToken? token = null, string version = "1") =>
            Cache.GetAsync(version, new Uri($"http://localhost/{Hash(bytes)}"), Hash(bytes), token ?? TestContext.Current.CancellationToken);

        internal string PathFor(byte[] bytes) => Path.Combine(Root, Hash(bytes));

        internal void Restart(long? maxCacheBytes = null, int? maxPackages = null)
        {
            Cache.Dispose();
            this.maxCacheBytes = maxCacheBytes ?? this.maxCacheBytes;
            this.maxPackages = maxPackages ?? this.maxPackages;
            Cache = CreateCache();
        }

        public void Dispose()
        {
            Cache.Dispose();
            Directory.Delete(Root, recursive: true);
        }

        private sealed class FixedTimeProvider : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
        }
    }
}
