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
            await writer.WriteAsync($$"""{"version":"1.0.0","entryAssembly":"{{Path.GetFileName(typeof(FixtureMarker).Assembly.Location)}}"}""".AsMemory(), token);
        }
        await using var fixture = new PackageCacheTests.CacheFixture(memory.ToArray());
        await using var pool = new TaskHostPool(NullLogger<TaskHostPool>.Instance);
        for (var run = 0; run < 2; run++)
        {
            var package = await fixture.Cache.GetAsync("1.0.0", PackageCacheTests.CacheFixture.Url, fixture.Sha, token);
            Assert.EndsWith(".dll", Assert.Single(package.Arguments));
            await pool.PrewarmAsync(package, token);
            var starts = pool.ProcessStartCount;
            var result = await pool.ExecuteAsync(package, new Dispatch
            {
                Version = "1.0.0",
                Task = new AttemptRef { RunId = Guid.NewGuid().ToString(), NodeKey = "work", TaskKey = "echo", Attempt = 1 },
                DeadlineAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(20)),
                InputsJson = ByteString.CopyFromUtf8("""{"source":{"value":7}}"""),
                VariablesJson = ByteString.CopyFromUtf8("""{"amount":5}""")
            }, ByteString.CopyFromUtf8("""{"previous":0}"""), cancellationToken: token);
            Assert.Equal(starts, pool.ProcessStartCount);
            Assert.Equal(TaskOutcome.Succeeded, result.Outcome);
            using var output = JsonDocument.Parse(result.OutputsJson.Memory);
            Assert.Equal(12, output.RootElement.GetProperty("result").GetInt32());
        }
        Assert.Equal(1, fixture.Handler.Count);
        Assert.Equal(2, pool.ProcessStartCount);
    }
}
