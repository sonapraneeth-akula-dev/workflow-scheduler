using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;
using Wf.Agent.Hosts;
using Wf.Contracts.Agent.V1;
using Wf.TaskHost.Fixture;

namespace Wf.Agent.Tests;

public sealed class PackageCacheIntegrationTests
{
    [Fact]
    public async Task Packaged_workflow_executes_two_runs_from_one_download_without_compiling()
    {
        var token = TestContext.Current.CancellationToken;
        var bytes = await WorkflowArchiveAsync();
        await using var fixture = new PackageCacheTests.CacheFixture(bytes);
        await using var pool = new TaskHostPool(NullLogger<TaskHostPool>.Instance);
        for (var run = 0; run < 2; run++)
        {
            using var package = await fixture.Cache.GetAsync("1.0.0", PackageCacheTests.CacheFixture.Url, fixture.Sha, token);
            Assert.EndsWith(".dll", Assert.Single(package.Arguments));
            await pool.PrewarmAsync(package, token);
            var starts = pool.ProcessStartCount;
            var result = await pool.ExecuteAsync(package, Dispatch(),
                ByteString.CopyFromUtf8("""{"previous":0}"""), cancellationToken: token);
            Assert.Equal(starts, pool.ProcessStartCount);
            Assert.Equal(TaskOutcome.Succeeded, result.Outcome);
            using var output = JsonDocument.Parse(result.OutputsJson.Memory);
            Assert.Equal(12, output.RootElement.GetProperty("result").GetInt32());
        }
        Assert.Equal(1, fixture.Handler.Count);
        Assert.Equal(2, pool.ProcessStartCount);
    }

    [Fact]
    public async Task Warm_unbound_and_executing_hosts_retain_dependencies_after_callers_release_packages()
    {
        var token = TestContext.Current.CancellationToken;
        var a = await WorkflowArchiveAsync(marker: "a");
        var b = await WorkflowArchiveAsync(marker: "b");
        using var fixture = new PackageCacheLruTests.BoundedCacheFixture([a, b], maxPackages: 1);
        await using var pool = new TaskHostPool(NullLogger<TaskHostPool>.Instance);
        using (var package = await fixture.GetAsync(a, version: "1.0.0"))
            await pool.PrewarmAsync(package, token);
        await Assert.ThrowsAsync<IOException>(() => fixture.GetAsync(b, version: "1.0.0"));
        Assert.True(File.Exists(Path.Combine(fixture.PathFor(a), "files", "Wf.Sdk.dll")));
        using var selected = await fixture.GetAsync(a, version: "1.0.0");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var execution = pool.ExecuteAsync(selected, Dispatch("delay"), cancellationToken: cancellation.Token);
        selected.Dispose();
        Assert.Empty(pool.WarmProcessIds);
        await Assert.ThrowsAsync<IOException>(() => fixture.GetAsync(b, version: "1.0.0"));
        await cancellation.CancelAsync();
        Assert.Equal(TaskOutcome.Cancelled, (await execution).Outcome);
        (await fixture.GetAsync(b, version: "1.0.0")).Dispose();
        Assert.False(Directory.Exists(fixture.PathFor(a)));
        Assert.Equal(1, pool.ProcessStartCount);
    }

    [Fact]
    public async Task Pool_disposal_releases_warm_package_pin_only_after_the_host_is_stopped()
    {
        var a = await WorkflowArchiveAsync(marker: "a");
        var b = await WorkflowArchiveAsync(marker: "b");
        using var fixture = new PackageCacheLruTests.BoundedCacheFixture([a, b], maxPackages: 1);
        await using var pool = new TaskHostPool(NullLogger<TaskHostPool>.Instance);
        using (var package = await fixture.GetAsync(a, version: "1.0.0"))
            await pool.PrewarmAsync(package, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<IOException>(() => fixture.GetAsync(b, version: "1.0.0"));
        await pool.DisposeAsync();
        (await fixture.GetAsync(b, version: "1.0.0")).Dispose();
        Assert.False(Directory.Exists(fixture.PathFor(a)));
    }

    [Fact]
    public async Task Failed_readiness_releases_the_host_pin_and_allows_later_eviction()
    {
        var a = await WorkflowArchiveAsync(version: "wrong", marker: "a");
        var b = await WorkflowArchiveAsync(marker: "b");
        using var fixture = new PackageCacheLruTests.BoundedCacheFixture([a, b], maxPackages: 1);
        await using var pool = new TaskHostPool(NullLogger<TaskHostPool>.Instance);
        using (var package = await fixture.GetAsync(a, version: "wrong"))
            await Assert.ThrowsAsync<InvalidDataException>(() => pool.PrewarmAsync(package, TestContext.Current.CancellationToken));
        (await fixture.GetAsync(b, version: "1.0.0")).Dispose();
        Assert.False(Directory.Exists(fixture.PathFor(a)));
        Assert.Empty(pool.WarmProcessIds);
    }

    private static Dispatch Dispatch(string task = "echo") => new()
    {
        Version = "1.0.0",
        Task = new AttemptRef { RunId = Guid.NewGuid().ToString(), NodeKey = "work", TaskKey = task, Attempt = 1 },
        DeadlineAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(20)),
        InputsJson = ByteString.CopyFromUtf8("""{"source":{"value":7}}"""),
        VariablesJson = ByteString.CopyFromUtf8("""{"amount":5}""")
    };

    private static async Task<byte[]> WorkflowArchiveAsync(string version = "1.0.0", string marker = "")
    {
        var token = TestContext.Current.CancellationToken;
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var path in Directory.EnumerateFiles(AppContext.BaseDirectory)
                .Where(path => Path.GetExtension(path) is ".dll" or ".json"))
            {
                var entry = archive.CreateEntry(Path.GetFileName(path));
                await using var source = File.OpenRead(path);
                await using var destination = entry.Open();
                await source.CopyToAsync(destination, token);
            }
            var manifest = archive.CreateEntry("wf-package.json");
            await using var writer = new StreamWriter(manifest.Open(), Encoding.UTF8);
            await writer.WriteAsync($$"""{"version":"{{version}}","entryAssembly":"{{Path.GetFileName(typeof(FixtureMarker).Assembly.Location)}}"}""".AsMemory(), token);
        }
        if (marker.Length != 0)
        {
            using var archive = new ZipArchive(memory, ZipArchiveMode.Update, leaveOpen: true);
            await using var writer = new StreamWriter(archive.CreateEntry("package-marker.txt").Open(), Encoding.UTF8);
            await writer.WriteAsync(marker.AsMemory(), token);
        }
        return memory.ToArray();
    }
}
