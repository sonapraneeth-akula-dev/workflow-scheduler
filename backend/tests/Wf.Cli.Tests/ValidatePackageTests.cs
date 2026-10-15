using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;
using Wf.Agent.Hosts;
using Wf.Agent.Packages;
using Wf.Cli.Packaging;
using Wf.Contracts.Agent.V1;
using Wf.Validation.Build;

namespace Wf.Cli.Tests;

[Collection("Validate")]
public sealed class ValidatePackageTests(ValidateFixture fixture)
{
    [Fact]
    public async Task Validate_two_uncached_real_sdk_builds_produce_identical_zip_hash_and_execute_from_agent_cache()
    {
        var output = fixture.NewDirectory();
        var first = await PackageBuilder.BuildAsync(fixture.Build, Path.Combine(output, "first.zip"), ValidateFixture.Token);
        var secondWork = fixture.NewDirectory();
        var rebuilt = await new ScriptBuildCache(Path.Combine(secondWork, "c"), Path.Combine(secondWork, "a"))
            .BuildAsync(new(Path.Combine(fixture.Source, "workflow.cs"), fixture.Source, fixture.Closure), ValidateFixture.Token);
        Assert.False(rebuilt.CacheHit);
        var second = await PackageBuilder.BuildAsync(rebuilt, Path.Combine(output, "second.zip"), ValidateFixture.Token);
        var firstBytes = await File.ReadAllBytesAsync(first.Path, ValidateFixture.Token);
        var secondBytes = await File.ReadAllBytesAsync(second.Path, ValidateFixture.Token);
        Assert.True(first.Sha256 == second.Sha256, "Different published files: " + string.Join(", ",
            Directory.EnumerateFiles(fixture.Build.PublishDirectory, "*", SearchOption.AllDirectories)
                .Where(file => !File.ReadAllBytes(file).SequenceEqual(File.ReadAllBytes(
                    Path.Combine(rebuilt.PublishDirectory, Path.GetRelativePath(fixture.Build.PublishDirectory, file)))))
                .Select(file => Path.GetRelativePath(fixture.Build.PublishDirectory, file))));
        Assert.Equal(firstBytes, secondBytes);
        Assert.Equal(first.Sha256, second.Sha256);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(firstBytes)), first.Sha256);
        Assert.Equal(firstBytes.LongLength, first.Size);
        Assert.Equal("Custom.Validate.Workflow.dll", Path.GetFileName(fixture.Build.AssemblyPath));
        AssertInventory(first.Path, fixture.Build.PublishDirectory);

        using var handler = new PackageHandler(firstBytes);
        using var cache = new PackageCache(fixture.NewDirectory(), NullLogger<PackageCache>.Instance, handler);
        await using var pool = new TaskHostPool(NullLogger<TaskHostPool>.Instance);
        for (var run = 0; run < 2; run++)
        {
            var package = await cache.GetAsync("1.0.0", new Uri("http://localhost/package"), first.Sha256, ValidateFixture.Token);
            Assert.Equal("Custom.Validate.Workflow.dll", Path.GetFileName(Assert.Single(package.Arguments)));
            await pool.PrewarmAsync(package, ValidateFixture.Token);
            var starts = pool.ProcessStartCount;
            var result = await pool.ExecuteAsync(package, new Dispatch
            {
                Version = "1.0.0",
                Task = new AttemptRef { RunId = Guid.NewGuid().ToString(), NodeKey = "work", TaskKey = "echo", Attempt = 1 },
                DeadlineAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(20)),
                InputsJson = ByteString.CopyFromUtf8("{}"),
                VariablesJson = ByteString.CopyFromUtf8("{}")
            }, cancellationToken: ValidateFixture.Token);
            Assert.Equal(starts, pool.ProcessStartCount);
            Assert.Equal(TaskOutcome.Succeeded, result.Outcome);
            using var document = JsonDocument.Parse(result.OutputsJson.Memory);
            Assert.Equal(42, document.RootElement.GetProperty("answer").GetInt32());
        }
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task Validate_zip_inventory_sorted_fixed_metadata_all_runtime_files_and_content_changes_affect_hash()
    {
        var build = fixture.SyntheticPublish(("z.json", "{}"), (Path.Combine("runtimes", "native", "helper.bin"), "native"),
            ("A.pdb", "debug"));
        var output = fixture.NewDirectory();
        var first = await PackageBuilder.BuildAsync(build, Path.Combine(output, "one.zip"), ValidateFixture.Token);
        foreach (var file in Directory.EnumerateFiles(build.PublishDirectory, "*", SearchOption.AllDirectories))
        {
            File.SetLastWriteTimeUtc(file, new DateTime(2025, 2, 3, 4, 5, 6, DateTimeKind.Utc));
            File.SetAttributes(file, FileAttributes.Archive);
        }
        var second = await PackageBuilder.BuildAsync(build, Path.Combine(output, "two.zip"), ValidateFixture.Token);
        Assert.Equal(first.Sha256, second.Sha256);
        AssertInventory(first.Path, build.PublishDirectory);
        File.WriteAllText(Path.Combine(build.PublishDirectory, "z.json"), """{"changed":true}""");
        var changed = await PackageBuilder.BuildAsync(build, first.Path, ValidateFixture.Token);
        Assert.NotEqual(first.Sha256, changed.Sha256);
        Assert.Empty(Directory.GetFiles(output, "*.partial"));
    }

    [Theory]
    [InlineData("outside")]
    [InlineData("missing")]
    [InlineData("extension")]
    [InlineData("manifest")]
    [InlineData("case-manifest")]
    [InlineData("output-inside")]
    public async Task Validate_package_rejects_invalid_entry_assembly_reserved_manifest_and_output_paths(string failure)
    {
        var build = fixture.SyntheticPublish();
        var output = Path.Combine(fixture.NewDirectory(), "package.zip");
        switch (failure)
        {
            case "outside": build = build with { AssemblyPath = Path.Combine(fixture.Root, "outside.dll") }; break;
            case "missing": File.Delete(build.AssemblyPath); break;
            case "extension": build = build with { AssemblyPath = Path.Combine(build.PublishDirectory, "custom.exe") }; break;
            case "manifest": File.WriteAllText(Path.Combine(build.PublishDirectory, "wf-package.json"), "{}"); break;
            case "case-manifest": File.WriteAllText(Path.Combine(build.PublishDirectory, "WF-PACKAGE.JSON"), "{}"); break;
            case "output-inside": output = Path.Combine(build.PublishDirectory, "self.zip"); break;
        }
        var exception = await Record.ExceptionAsync(() => PackageBuilder.BuildAsync(build, output, ValidateFixture.Token));
        Assert.True(exception is InvalidDataException or ArgumentException, exception?.ToString());
        Assert.False(File.Exists(output));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Validate_package_rejects_linked_publish_files_and_directories(bool rootLink)
    {
        var build = fixture.SyntheticPublish();
        var external = fixture.NewDirectory();
        File.WriteAllText(Path.Combine(external, "must-not-package.txt"), "secret");
        var link = Path.Combine(fixture.NewDirectory(), "link");
        if (OperatingSystem.IsWindows())
        {
            using var process = Process.Start(new ProcessStartInfo("cmd.exe")
            {
                ArgumentList = { "/d", "/c", "mklink", "/J", link, rootLink ? build.PublishDirectory : external },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            })!;
            await process.WaitForExitAsync(ValidateFixture.Token);
            Assert.Equal(0, process.ExitCode);
        }
        else Directory.CreateSymbolicLink(link, rootLink ? build.PublishDirectory : external);
        try
        {
            if (rootLink)
                build = build with { PublishDirectory = link, AssemblyPath = Path.Combine(link, "custom.dll") };
            else
            {
                var child = Path.Combine(build.PublishDirectory, "nested");
                Directory.Move(link, child);
                link = child;
            }
            var output = Path.Combine(fixture.NewDirectory(), "package.zip");
            await Assert.ThrowsAsync<InvalidDataException>(() => PackageBuilder.BuildAsync(build, output, ValidateFixture.Token));
            Assert.False(File.Exists(output));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public async Task Validate_package_io_failure_preserves_existing_destination_and_cleans_staging()
    {
        var build = fixture.SyntheticPublish();
        var directory = fixture.NewDirectory();
        var output = Path.Combine(directory, "existing.zip");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "keep"), "existing");
        var exception = await Record.ExceptionAsync(() => PackageBuilder.BuildAsync(build, output, ValidateFixture.Token));
        Assert.True(exception is IOException or UnauthorizedAccessException, exception?.ToString());
        Assert.Equal("existing", File.ReadAllText(Path.Combine(output, "keep")));
        Assert.Empty(Directory.GetFiles(directory, "*.partial"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Validate_package_rejects_agent_incompatible_expanded_size_and_file_count(bool countLimit)
    {
        var build = fixture.SyntheticPublish();
        if (countLimit)
        {
            for (var index = 0; index < 9999; index++)
                File.WriteAllBytes(Path.Combine(build.PublishDirectory, $"{index}.bin"), []);
        }
        else
        {
            await using var file = new FileStream(Path.Combine(build.PublishDirectory, "oversize.bin"), FileMode.CreateNew);
            file.SetLength(PackageCache.MaxExpandedBytes + 1);
        }
        var directory = fixture.NewDirectory();
        var output = Path.Combine(directory, "package.zip");
        await Assert.ThrowsAsync<InvalidDataException>(() => PackageBuilder.BuildAsync(build, output, ValidateFixture.Token));
        Assert.False(File.Exists(output));
        Assert.Empty(Directory.GetFiles(directory, "*.partial"));
    }

    [Fact]
    public async Task Validate_package_cancellation_after_staging_preserves_existing_output_and_cleans_partial()
    {
        var build = fixture.SyntheticPublish();
        var large = Path.Combine(build.PublishDirectory, "large.bin");
        await using (var file = new FileStream(large, FileMode.CreateNew))
            file.SetLength(128 * 1024 * 1024);
        var directory = fixture.NewDirectory();
        var output = Path.Combine(directory, "existing.zip");
        File.WriteAllText(output, "previous");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ValidateFixture.Token);
        var packaging = PackageBuilder.BuildAsync(build, output, cancellation.Token);
        while (!Directory.EnumerateFiles(directory, "*.partial").Any())
        {
            Assert.False(packaging.IsCompleted, "Packaging finished before cancellation reached staging.");
            await Task.Delay(1, ValidateFixture.Token);
        }
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => packaging);
        Assert.Equal("previous", File.ReadAllText(output));
        Assert.Empty(Directory.GetFiles(directory, "*.partial"));
    }

    [Fact]
    public async Task Validate_package_detects_publish_mutation_and_leaves_no_output()
    {
        var build = fixture.SyntheticPublish();
        await using (var file = new FileStream(Path.Combine(build.PublishDirectory, "large.bin"), FileMode.CreateNew))
            file.SetLength(128 * 1024 * 1024);
        var directory = fixture.NewDirectory();
        var output = Path.Combine(directory, "package.zip");
        var packaging = PackageBuilder.BuildAsync(build, output, ValidateFixture.Token);
        while (!Directory.EnumerateFiles(directory, "*.partial").Any())
        {
            Assert.False(packaging.IsCompleted, "Packaging finished before inventory mutation could be exercised.");
            await Task.Delay(1, ValidateFixture.Token);
        }
        File.WriteAllText(Path.Combine(build.PublishDirectory, "new.json"), "{}");
        await Assert.ThrowsAsync<IOException>(() => packaging);
        Assert.False(File.Exists(output));
        Assert.Empty(Directory.GetFiles(directory, "*.partial"));
    }

    private static void AssertInventory(string path, string publish)
    {
        using var archive = ZipFile.OpenRead(path);
        var expected = Directory.EnumerateFiles(publish, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(publish, file).Replace(Path.DirectorySeparatorChar, '/'))
            .Append("wf-package.json").Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, archive.Entries.Select(entry => entry.FullName).ToArray());
        Assert.All(archive.Entries, entry =>
        {
            Assert.Equal(new DateTime(1980, 1, 1), entry.LastWriteTime.DateTime);
            Assert.Equal(0, entry.ExternalAttributes);
            Assert.Equal(entry.Length, entry.CompressedLength);
        });
        using var json = JsonDocument.Parse(archive.GetEntry("wf-package.json")!.Open());
        Assert.Equal(2, json.RootElement.EnumerateObject().Count());
        Assert.Equal("1.0.0", json.RootElement.GetProperty("version").GetString());
        Assert.EndsWith(".dll", json.RootElement.GetProperty("entryAssembly").GetString());
        foreach (var entry in archive.Entries.Where(entry => entry.FullName != "wf-package.json"))
        {
            using var packaged = entry.Open();
            using var source = File.OpenRead(Path.Combine(publish, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            Assert.Equal(SHA256.HashData(source), SHA256.HashData(packaged));
        }
    }

    private sealed class PackageHandler(byte[] bytes) : HttpMessageHandler
    {
        internal int Count { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }
}
