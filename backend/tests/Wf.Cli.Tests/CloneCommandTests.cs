using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Wf.Cli.Commands;

namespace Wf.Cli.Tests;

[CollectionDefinition("Clone", DisableParallelization = true)]
public sealed class CloneCollection;

[Collection("Clone")]
public sealed class CloneCommandTests : IDisposable
{
    private const string Version = "019ed132-2f44-7000-8000-000000000001";
    private readonly string root = Path.Combine(Directory.GetCurrentDirectory(), ".agent-artifacts", "clone-clients-3adec91f", "u", Guid.NewGuid().ToString("N"));
    private readonly string? engine = Environment.GetEnvironmentVariable("WF_ENGINE_URL");
    private readonly string? token = Environment.GetEnvironmentVariable("WF_ACCESS_TOKEN");
    private string Destination => Path.Combine(root, "teams", "target", "release", "experiment");
    private string[] Arguments => ["team/type/source", "--version", Version, "--new-name", "experiment", "--target-type", "target/release", "--repo", root];

    public CloneCommandTests()
    {
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("WF_ENGINE_URL", "http://127.0.0.1:1");
        Environment.SetEnvironmentVariable("WF_ACCESS_TOKEN", null);
    }

    [Fact]
    public async Task Clone_exact_request_downloads_six_files_atomically_without_executing_source()
    {
        var archive = Archive();
        await using var server = new PublishHttpServer(_ => Reply(archive));
        Environment.SetEnvironmentVariable("WF_ENGINE_URL", server.Origin);
        Environment.SetEnvironmentVariable("WF_ACCESS_TOKEN", "clone-secret");
        var result = await Run(Arguments);
        Assert.Equal(0, result.Exit);
        Assert.Empty(result.Error);
        Assert.Contains("Source not executed", result.Output);
        Assert.Contains("team/type/source@" + Version, result.Output);
        var request = Assert.Single(server.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal($"/api/v1/workflows/team/type/source/versions/{Version}/clone", request.Path);
        Assert.Equal("Bearer clone-secret", request.Headers["Authorization"]);
        using var json = JsonDocument.Parse(request.Body);
        Assert.Equal(["newName", "targetType"], json.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal("experiment", json.RootElement.GetProperty("newName").GetString());
        Assert.Equal("target/release", json.RootElement.GetProperty("targetType").GetString());
        Assert.Equal(6, Directory.GetFiles(Destination, "*", SearchOption.AllDirectories).Length);
        Assert.Equal("DO NOT EXECUTE " + Version, await File.ReadAllTextAsync(Path.Combine(Destination, "workflow.cs"), TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(Destination)!, ".wf-clone-*"));
        Assert.Equal(2, (await Run(Arguments)).Exit);
        Assert.Single(server.Requests);
        Assert.Equal("DO NOT EXECUTE " + Version, await File.ReadAllTextAsync(Path.Combine(Destination, "workflow.cs"), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("--version", "latest")]
    [InlineData("--version", "00000000-0000-0000-0000-000000000000")]
    [InlineData("--version", "019ED132-2f44-7000-8000-000000000001")]
    [InlineData("--new-name", "../escape")]
    [InlineData("--new-name", "NUL.txt")]
    [InlineData("--new-name", "COM1")]
    [InlineData("--new-name", "file:stream")]
    [InlineData("--new-name", "name.")]
    [InlineData("--new-name", "name ")]
    [InlineData("--target-type", "../release")]
    [InlineData("--target-type", "target/release/extra")]
    [InlineData("--target-type", "target\\release")]
    [InlineData("--engine", "http://remote.example")]
    [InlineData("--engine", "https://example.test/private")]
    [InlineData("--engine", "https://example.test?token=never-echo")]
    [InlineData("--engine", "https://user:never-echo@example.test")]
    [InlineData("--unknown", "never-echo")]
    public async Task Clone_rejects_invalid_options_before_any_files_or_requests(string option, string value)
    {
        var args = Arguments.ToList();
        var index = args.IndexOf(option);
        if (index >= 0) args[index + 1] = value;
        else args.AddRange([option, value]);
        var result = await Run(args);
        Assert.Equal(2, result.Exit);
        Assert.Contains("Usage: wf clone", result.Error);
        Assert.DoesNotContain("never-echo", result.Error);
        Assert.False(Path.Exists(Destination));
    }

    [Theory]
    [InlineData("")]
    [InlineData("team/type/source")]
    [InlineData("team/type/source --version")]
    [InlineData("team/type/source --version x --version y --new-name z --target-type t/r")]
    public async Task Clone_rejects_missing_duplicate_options(string arguments) =>
        Assert.Equal(2, (await Run(arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries))).Exit);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("token with spaces")]
    [InlineData("never-echo\r\nInjected: true")]
    public async Task Clone_remote_auth_is_required_and_never_echoed(string? value)
    {
        Environment.SetEnvironmentVariable("WF_ACCESS_TOKEN", value);
        var result = await Run([.. Arguments, "--engine", "https://example.test"]);
        Assert.Equal(2, result.Exit);
        Assert.DoesNotContain("never-echo", result.Error);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(409)]
    [InlineData(503)]
    [InlineData(302)]
    public async Task Clone_rejections_and_redirects_never_write_or_retry(int status)
    {
        await using var server = new PublishHttpServer(_ => new(status, "never-echo", Location: "https://example.test"));
        var result = await Run([.. Arguments, "--engine", server.Origin]);
        Assert.Equal(1, result.Exit);
        Assert.Contains("HTTP " + status, result.Error);
        Assert.DoesNotContain("never-echo", result.Error);
        Assert.Single(server.Requests);
        Assert.False(Path.Exists(Destination));
    }

    [Theory]
    [InlineData("code", "unsupported_clone_source", true)]
    [InlineData("code", "clone_target_exists", true)]
    [InlineData("reason", "insufficient_scope_role", true)]
    [InlineData("code", "never-echo", false)]
    public async Task Clone_reports_only_known_problem_codes_not_untrusted_details(string field, string code, bool known)
    {
        await using var server = new PublishHttpServer(_ => new(400,
            $$"""{"{{field}}":"{{code}}","detail":"never-echo"}""", ContentType: "application/problem+json"));
        var result = await Run([.. Arguments, "--engine", server.Origin]);
        Assert.Equal(1, result.Exit);
        Assert.Equal(known, result.Error.Contains("(" + code + ")", StringComparison.Ordinal));
        Assert.DoesNotContain("never-echo", result.Error);
    }

    [Fact]
    public async Task Clone_escapes_each_source_path_segment_without_using_archive_filename_as_destination()
    {
        await using var server = new PublishHttpServer(_ => Reply(Archive()));
        var arguments = Arguments.ToArray();
        arguments[0] = "team δ/type%/source#";
        Assert.Equal(0, (await Run([.. arguments, "--engine", server.Origin])).Exit);
        Assert.Equal($"/api/v1/workflows/team%20%CE%B4/type%25/source%23/versions/{Version}/clone", Assert.Single(server.Requests).Path);
        Assert.True(File.Exists(Path.Combine(Destination, "workflow.cs")));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("/rooted")]
    [InlineData("C:/absolute")]
    [InlineData("schemas\\schedules.schema.json")]
    [InlineData("WORKFLOW.CS")]
    [InlineData("workflow.cs")]
    public async Task Clone_rejects_traversal_unknown_case_and_colliding_entries(string entry)
    {
        var bytes = Archive(extra: entry);
        await using var server = new PublishHttpServer(_ => Reply(bytes));
        Assert.Equal(2, (await Run([.. Arguments, "--engine", server.Origin])).Exit);
        Assert.False(Path.Exists(Destination));
    }

    [Theory]
    [InlineData("link")]
    [InlineData("missing")]
    [InlineData("expanded")]
    [InlineData("download")]
    [InlineData("truncated")]
    [InlineData("invalid")]
    [InlineData("headers")]
    [InlineData("drop")]
    [InlineData("corrupt")]
    public async Task Clone_rejects_unsafe_or_failed_download_without_partial_destination(string mode)
    {
        var bytes = mode switch
        {
            "download" => new byte[2 * 1024 * 1024 + 1],
            "invalid" => Encoding.UTF8.GetBytes("not zip"),
            _ => Archive(mode: mode)
        };
        if (mode == "truncated") bytes = bytes[..^20];
        if (mode == "corrupt")
        {
            var name = Encoding.UTF8.GetBytes("README.md");
            var offset = bytes.AsSpan().IndexOf(name);
            Assert.True(offset > 0);
            // Corrupt the final compressed payload, after five entries can already have been staged.
            bytes[offset + name.Length] = 0xff;
        }
        await using var server = new PublishHttpServer(_ => mode == "drop" ? new(200, Drop: true) :
            mode == "headers" ? new(200, Bytes: bytes) : Reply(bytes));
        Assert.Equal(2, (await Run([.. Arguments, "--engine", server.Origin])).Exit);
        Assert.False(Path.Exists(Destination));
        Assert.Empty(Directory.GetDirectories(root, ".wf-clone-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Clone_publication_race_keeps_existing_directory_and_cleans_partial_staging()
    {
        await using var server = new PublishHttpServer(_ =>
        {
            Directory.CreateDirectory(Destination);
            File.WriteAllText(Path.Combine(Destination, "keep.txt"), "original");
            return Reply(Archive());
        });
        Assert.Equal(2, (await Run([.. Arguments, "--engine", server.Origin])).Exit);
        Assert.Equal(["keep.txt"], Directory.GetFiles(Destination).Select(Path.GetFileName));
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(Destination, "keep.txt"), TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetDirectories(root, ".wf-clone-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Clone_rejects_existing_file_and_linked_repository()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Destination)!);
        await File.WriteAllTextAsync(Destination, "keep", TestContext.Current.CancellationToken);
        Assert.Equal(2, (await Run(Arguments)).Exit);
        Assert.Equal("keep", await File.ReadAllTextAsync(Destination, TestContext.Current.CancellationToken));
        var link = Path.Combine(root, "linked");
        Directory.CreateSymbolicLink(link, Path.GetDirectoryName(Destination)!);
        var args = Arguments.ToArray();
        args[^1] = link;
        Assert.Equal(2, (await Run(args)).Exit);
        Directory.Delete(link);
    }

    [Fact]
    public async Task Clone_pre_cancel_and_help_do_not_contact_engine()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Equal(130, (await Run(Arguments, cancelled.Token)).Exit);
        Environment.SetEnvironmentVariable("WF_ENGINE_URL", null);
        Assert.Equal(0, (await Run(["--help"])).Exit);
        Assert.False(Path.Exists(Destination));
    }

    private static PublishHttpServer.Reply Reply(byte[] bytes) => new(200, ContentType: "application/zip", Bytes: bytes,
        ExtraHeaders: "Content-Disposition: attachment; filename=ignored.zip\r\nCache-Control: no-store\r\n");

    private static byte[] Archive(string? extra = null, string? mode = null)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            string[] names = ["workflow.cs", "schedules.json", "schemas/schedules.schema.json", "WorkflowClone.Tests.csproj", "Tests/CloneTests.cs", "README.md"];
            foreach (var name in names.Where(name => mode != "missing" || name != "README.md").Concat(extra is null ? [] : new[] { extra }))
            {
                var entry = archive.CreateEntry(name, CompressionLevel.SmallestSize);
                entry.ExternalAttributes = mode == "link" && name == "workflow.cs" ? unchecked((int)0xa1ff0000) : 0;
                using var writer = new StreamWriter(entry.Open());
                writer.Write(mode == "expanded" ? new string('x', 2 * 1024 * 1024 + 1) : "DO NOT EXECUTE " + Version);
            }
        }
        return output.ToArray();
    }

    private static async Task<(int Exit, string Output, string Error)> Run(IReadOnlyList<string> args, CancellationToken? token = null)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var result = await CloneCommand.RunAsync(args, output, error, token ?? TestContext.Current.CancellationToken);
        return (result, output.ToString(), error.ToString());
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("WF_ENGINE_URL", engine);
        Environment.SetEnvironmentVariable("WF_ACCESS_TOKEN", token);
        Directory.Delete(root, recursive: true);
    }
}
