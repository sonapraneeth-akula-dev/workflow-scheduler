using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.IO.Compression;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Microsoft.IdentityModel.Validators;
using Npgsql;
using Wf.Contracts.Registry;
using Wf.ControlPlane.Api;
using Wf.ControlPlane.Auth;
using Wf.ControlPlane.Engine.Registry;
using Wf.ControlPlane.Registration;
using Wf.ControlPlane.Storage;

namespace Wf.Integration.Tests.Registration;

public sealed partial class PublishRegistrationTests(RegistrationDatabase database, ITestOutputHelper output)
    : IClassFixture<RegistrationDatabase>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Main_branch_merge_runs_template_publication_and_exposes_exact_new_version_through_real_API()
        => await PublishMergedVersionsAsync(1);

    private async Task<MergeTiming[]> PublishMergedVersionsAsync(int mergeCount)
    {
        var root = Path.Combine(Repository, ".agent-artifacts", "merge-publication", Guid.NewGuid().ToString("N")[..8]);
        var checkout = Path.Combine(root, "r");
        var workflowDirectory = Path.Combine(checkout, "workflow");
        Directory.CreateDirectory(workflowDirectory);
        try
        {
            var packages = await PreparePackageConsumer(root, checkout);
            await using var server = await PublishServer.Start(database.DataSource, Path.Combine(root, "blobs"));
            using var cache = new ActiveVersionCache(database.DataSource,
                NullLogger<ActiveVersionCache>.Instance, TimeProvider.System);
            await cache.StartAsync(Token);
            try
            {
                var registrarToken = server.AccessToken();
                var team = "merge-" + Guid.NewGuid().ToString("N");
                var identity = team + "/release/workflow";
                await WaitForVersion(null);
                var script = $$"""
                #:package Wf.Sdk
                #:property PublishAot=false
                #:property AssemblyName=Merge.Integration.Workflow
                using Wf.Sdk;
                var workflow = Workflow.Define("{{identity}}", "1.0.0", w => w.Type("release")
                    .Node("work", n => n.Task("echo", ctx => ctx.Outputs["answer"] = 42)));
                return await workflow.RunAsync(args);
                """;
                var source = Path.Combine(workflowDirectory, "workflow.cs");
                await File.WriteAllTextAsync(source, script, new UTF8Encoding(false), Token);
                await File.WriteAllBytesAsync(Path.Combine(workflowDirectory, "team.json"),
                    JsonSerializer.SerializeToUtf8Bytes(new TeamRegistry(team, [new(team, "release", "Merge fixtures", new([]))]),
                        TeamRegistryJsonContext.Default.TeamRegistry), Token);
                await File.WriteAllTextAsync(Path.Combine(checkout, ".gitignore"), "obj/\nbin/\n", Token);
                await Git("init", "--quiet", "--initial-branch=main");
                await Git("config", "user.name", "Merge gate fixture");
                await Git("config", "user.email", "merge-gate@example.invalid");
                await Git("config", "commit.gpgsign", "false");
                await Git("add", "--all");
                await Git("commit", "--quiet", "-m", "test: Initial workflow");
                var before = (await Git("rev-parse", "HEAD")).Trim();
                var initial = await External("dotnet",
                    [typeof(Wf.Cli.Program).Assembly.Location, "publish", workflowDirectory, "--engine", server.Origin,
                        "--git-commit", before, "--git-author", "Merge gate fixture", "--input", checkout],
                    Repository, registrarToken, new() { ["NUGET_PACKAGES"] = packages });
                Assert.True(initial.Exit == 0, initial.Error);
                await WaitForVersion("1.0.0");
                var helper = Path.Combine(Repository, "backend", "src", "Wf.Templates", "workflows-repo",
                    ".github", "scripts", "register_on_merge.py");
                // Resolve the installed tool to this build; selection and publication use the unchanged template helper.
                const string driver = """
                import subprocess, sys
                from pathlib import Path
                helper, cli = sys.argv[1:3]
                sys.path.insert(0, str(Path(helper).parent))
                import register_on_merge as gate
                def built_tool(repository, *arguments):
                    if arguments[:5] != ("dotnet", "tool", "run", "wf", "--"):
                        raise ValueError("Unexpected publication command")
                    subprocess.run(["dotnet", cli, *arguments[5:]], cwd=repository, check=True)
                gate.run = built_tool
                sys.argv = [helper, "--publish"]
                gate.main()
                """;
                var timings = new MergeTiming[mergeCount];
                using var client = new HttpClient { BaseAddress = new(server.Origin) };
                client.DefaultRequestHeaders.Authorization = new("Bearer", server.AccessToken("Admin"));
                for (var index = 1; index <= mergeCount; index++)
                {
                    var semver = "1.0." + index;
                    var branch = "feat/new-version-" + index;
                    await Git("checkout", "--quiet", "-b", branch);
                    await File.WriteAllTextAsync(source, script.Replace("\"1.0.0\"", "\"" + semver + "\"",
                        StringComparison.Ordinal), new UTF8Encoding(false), Token);
                    await Git("add", "--all");
                    await Git("commit", "--quiet", "-m", "test: New explicit workflow version " + semver);
                    await Git("checkout", "--quiet", "main");
                    var merged = Stopwatch.StartNew();
                    await Git("merge", "--quiet", "--no-ff", "--no-edit", branch);
                    var head = (await Git("rev-parse", "HEAD")).Trim();
                    var result = await External(OperatingSystem.IsWindows() ? "python" : "python3",
                    ["-c", driver, helper, typeof(Wf.Cli.Program).Assembly.Location], checkout, registrarToken,
                    new()
                    {
                        ["PUBLISH_BASE_SHA"] = before,
                        ["PUBLISH_HEAD_SHA"] = head,
                        ["GITHUB_SHA"] = head,
                        ["GITHUB_REF"] = "refs/heads/main",
                        ["GITHUB_EVENT_NAME"] = "push",
                        ["WF_ENGINE_URL"] = server.Origin,
                        ["NUGET_PACKAGES"] = packages
                    });
                    Assert.True(result.Exit == 0, result.Error);
                    Assert.Contains("Registered: " + identity + "@" + semver, result.Output);
                    Assert.DoesNotContain(registrarToken, result.Output + result.Error);
                    await WaitForVersion(semver);
                    var mergeToActive = merged.Elapsed;
                    var active = cache.GetActive(identity);
                    // Both timestamps come from PostgreSQL; now() precedes registration commit, so this is conservative.
                    var registrationToActive = Assert.IsType<TimeSpan>(await Value(
                        "SELECT clock_timestamp()-created_at FROM workflow_versions WHERE id=$1", active.VersionId));
                    timings[index - 1] = new(mergeToActive, registrationToActive);
                    output.WriteLine($"Merge {index}/{mergeCount}: {semver}, merge-to-active={mergeToActive.TotalMilliseconds:F1} ms, " +
                        $"registration-to-active upper bound={registrationToActive.TotalMilliseconds:F1} ms; commit={head}.");
                    using var response = await client.GetAsync($"/api/v1/workflows/{identity}/versions", Token);
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(Token));
                    var items = document.RootElement.GetProperty("items").EnumerateArray().ToArray();
                    Assert.Equal(Enumerable.Range(0, index + 1).Reverse().Select(value => "1.0." + value),
                        items.Select(item => item.GetProperty("version").GetString()));
                    var version = items[0].GetProperty("versionId").GetGuid();
                    Assert.Equal(version, document.RootElement.GetProperty("activeVersionId").GetGuid());
                    Assert.Equal(version, active.VersionId);
                    Assert.Equal(head, await Value("SELECT git_commit FROM workflow_versions WHERE id=$1", version));
                    Assert.True(registrationToActive >= TimeSpan.Zero && registrationToActive <= TimeSpan.FromSeconds(5),
                        $"Merge {index}: registration-to-active upper bound {registrationToActive} exceeds five seconds.");
                    before = head;
                }
                return timings;

                async Task<string> Git(params string[] arguments)
                {
                    var result = await External("git", ["--no-pager", .. arguments], checkout, null);
                    Assert.True(result.Exit == 0, result.Error);
                    return result.Output;
                }

                async Task WaitForVersion(string? version)
                {
                    var elapsed = Stopwatch.StartNew();
                    while (!cache.IsReady || version is not null &&
                        (!HasVersion() || cache.GetActive(identity).Manifest.Version != version))
                    {
                        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5),
                            $"Active version {version ?? "(cache ready)"} was not observed within five seconds.");
                        await Task.Delay(10, Token);
                    }
                }

                bool HasVersion()
                {
                    try { _ = cache.GetActive(identity); return true; }
                    catch (KeyNotFoundException) { return false; }
                }
            }
            finally { await cache.StopAsync(Token); }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Publish_actual_cli_signed_jwt_kestrel_postgres_registers_exact_ci_package_source_provenance_and_noop()
    {
        var root = Path.Combine(Repository, ".agent-artifacts", "cli-publish-3adec91f", "i", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            await using var server = await PublishServer.Start(database.DataSource, Path.Combine(root, "blobs"));
            var source = Path.Combine(root, "source");
            Directory.CreateDirectory(source);
            var team = "publish-" + Guid.NewGuid().ToString("N");
            var workflow = team + "/release/workflow";
            var script = $$"""
                #:project {{Path.Combine(Repository, "backend", "src", "Wf.Sdk", "Wf.Sdk.csproj")}}
                #:property PublishAot=false
                #:property AssemblyName=Publish.Integration.Workflow
                using Wf.Sdk;
                var workflow = Workflow.Define("{{workflow}}", "1.0.0", w => w.Type("release")
                    .Node("work", n => n.Task("echo", ctx => ctx.Outputs["answer"] = 42)));
                return await workflow.RunAsync(args);
                """;
            await File.WriteAllTextAsync(Path.Combine(source, "workflow.cs"), script, new UTF8Encoding(false), Token);
            var registry = new TeamRegistry(team, [new(team, "release", "Release workflows", new([]))]);
            await File.WriteAllBytesAsync(Path.Combine(source, "team.json"),
                JsonSerializer.SerializeToUtf8Bytes(registry, TeamRegistryJsonContext.Default.TeamRegistry), Token);
            var common = new[] { source, "--work-directory", Path.Combine(root, "work") }
                .Concat(new[] { "Wf.Sdk", "Wf.Contracts", "Wf.Engine.Core", "Wf.Validation" }
                    .Select(name => Path.Combine(Repository, "backend", "src", name))
                    .Append(Path.Combine(Repository, "backend", "proto"))
                    .SelectMany(path => new[] { "--input", path })).ToArray();
            var commit = new string('a', 40);
            var arguments = new[] { "publish" }.Concat(common).Concat([
                "--engine", server.Origin, "--git-commit", commit, "--git-author", "CLI Integration Author",
                "--change-note", "Actual signed JWT publication"
            ]).ToArray();
            var ciPackage = Path.Combine(root, "ci.zip");
            var ci = await Process(["validate", .. common, "--package", ciPackage], null);
            Assert.True(ci.Exit == 0, ci.Error);
            var bytes = await File.ReadAllBytesAsync(ciPackage, Token);
            var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));

            var denied = await Process(arguments, server.AccessToken("NotRegistrar"));
            Assert.Equal(1, denied.Exit);
            Assert.Contains("HTTP 403", denied.Error);
            Assert.Empty(denied.Output);
            Assert.Equal(0L, await Value("SELECT count(*) FROM workflow_versions WHERE workflow_id=$1", workflow));

            var published = await Process(arguments, server.AccessToken());
            Assert.True(published.Exit == 0, published.Error);
            Assert.Empty(published.Error);
            Assert.Contains("Registered: " + workflow + "@1.0.0", published.Output);
            Assert.Contains("SHA256: " + sha, published.Output);
            var version = Assert.IsType<Guid>(await Value("SELECT active_version_id FROM workflows WHERE id=$1", workflow));
            Assert.Equal(version, await Value("SELECT latest_version_id FROM workflows WHERE id=$1", workflow));
            await using (var query = database.DataSource.CreateCommand("""
                SELECT package_sha256,package_size,git_commit,git_author,registered_by,change_note,source_sha256,source_size
                FROM workflow_versions WHERE id=$1
                """))
            {
                query.Parameters.AddWithValue(version);
                await using var reader = await query.ExecuteReaderAsync(Token);
                Assert.True(await reader.ReadAsync(Token));
                Assert.Equal(Convert.FromHexString(sha), reader.GetFieldValue<byte[]>(0));
                Assert.Equal(bytes.Length, reader.GetInt64(1));
                Assert.Equal(commit, reader.GetString(2));
                Assert.Equal("CLI Integration Author", reader.GetString(3));
                Assert.Equal(PublishServer.Actor.ToString("D"), reader.GetString(4));
                Assert.Equal("Actual signed JWT publication", reader.GetString(5));
                using var downloaded = new MemoryStream();
                await server.Blobs.DownloadAsync(new(BlobKind.Packages, sha, bytes.Length), downloaded, Token);
                Assert.Equal(bytes, downloaded.ToArray());
                using var sourceZip = new MemoryStream();
                await server.Blobs.DownloadAsync(new(BlobKind.Sources,
                    Convert.ToHexStringLower(reader.GetFieldValue<byte[]>(6)), reader.GetInt64(7)), sourceZip, Token);
                sourceZip.Position = 0;
                using var archive = new ZipArchive(sourceZip, ZipArchiveMode.Read);
                using var text = new StreamReader(Assert.Single(archive.Entries).Open());
                Assert.Equal(script, await text.ReadToEndAsync(Token));
            }
            var repeated = await Process(arguments, server.AccessToken());
            Assert.True(repeated.Exit == 0, repeated.Error);
            Assert.Contains("Already registered:", repeated.Output);
            Assert.Equal(1L, await Value("SELECT count(*) FROM workflow_versions WHERE workflow_id=$1", workflow));
            Assert.Equal(1L, await Value("SELECT count(*) FROM audit_log WHERE target_id=$1 AND actor=$2",
                workflow, PublishServer.Actor.ToString("D")));
            Assert.Equal(1L, await Value("SELECT count(*) FROM registry_snapshots WHERE team_id=$1", team));

            var schedules = Path.Combine(source, "schedules.json");
            await File.WriteAllTextAsync(schedules, "{}", Token);
            var invalidCi = await Process(["validate", .. common], null);
            var invalidPublish = await Process([.. arguments, "--schedule-source-path", $"teams/{team}/release/workflow/schedules.json"],
                server.AccessToken());
            Assert.Equal(1, invalidCi.Exit);
            Assert.Equal(1, invalidPublish.Exit);
            Assert.Equal(invalidCi.Error, invalidPublish.Error);
            Assert.Contains("WFV011", invalidPublish.Error);
            Assert.Empty(invalidPublish.Output);
            Assert.Equal(1L, await Value("SELECT count(*) FROM workflow_versions WHERE workflow_id=$1", workflow));
            Assert.Equal(version, await Value("SELECT active_version_id FROM workflows WHERE id=$1", workflow));

            File.Delete(schedules);
            var changedSource = script.Replace("ctx.Outputs[\"answer\"] = 42", "ctx.Outputs[\"answer\"] = 43", StringComparison.Ordinal);
            Assert.NotEqual(script, changedSource);
            await File.WriteAllTextAsync(Path.Combine(source, "workflow.cs"), changedSource, new UTF8Encoding(false), Token);
            var missingBump = await Process(arguments, server.AccessToken());
            Assert.Equal(1, missingBump.Exit);
            Assert.Contains("HTTP 409", missingBump.Error);
            Assert.Empty(missingBump.Output);
            Assert.Equal(1L, await Value("SELECT count(*) FROM workflow_versions WHERE workflow_id=$1", workflow));
            Assert.Equal(version, await Value("SELECT active_version_id FROM workflows WHERE id=$1", workflow));
            Assert.Equal(version, await Value("SELECT latest_version_id FROM workflows WHERE id=$1", workflow));
            Assert.Equal("1.0.0", await Value("SELECT semver FROM workflow_versions WHERE id=$1", version));
            Assert.Equal(1L, await Value("SELECT count(*) FROM audit_log WHERE target_id=$1", workflow));

            var bumpedSource = changedSource.Replace("\"1.0.0\"", "\"1.0.1\"", StringComparison.Ordinal);
            Assert.NotEqual(changedSource, bumpedSource);
            await File.WriteAllTextAsync(Path.Combine(source, "workflow.cs"), bumpedSource, new UTF8Encoding(false), Token);
            var bumped = await Process(arguments, server.AccessToken());
            Assert.True(bumped.Exit == 0, bumped.Error);
            Assert.Contains("Registered: " + workflow + "@1.0.1", bumped.Output);
            Assert.Empty(bumped.Error);
            var nextVersion = Assert.IsType<Guid>(await Value("SELECT active_version_id FROM workflows WHERE id=$1", workflow));
            Assert.NotEqual(version, nextVersion);
            Assert.Equal(nextVersion, await Value("SELECT latest_version_id FROM workflows WHERE id=$1", workflow));
            Assert.Equal("1.0.1", await Value("SELECT semver FROM workflow_versions WHERE id=$1", nextVersion));
            Assert.Equal("1.0.0", await Value("SELECT semver FROM workflow_versions WHERE id=$1", version));
            Assert.Equal(2L, await Value("SELECT count(*) FROM workflow_versions WHERE workflow_id=$1", workflow));
            Assert.Equal(2L, await Value("SELECT count(*) FROM audit_log WHERE target_id=$1", workflow));
            Assert.Equal(true, await Value("SELECT package_sha256<>$2 FROM workflow_versions WHERE id=$1", nextVersion, Convert.FromHexString(sha)));
            output.WriteLine("Changed actual workflow task code: same code-owned SemVer rejected with 409 and no version/audit/pointer writes; manual Workflow.Define version bump to 1.0.1 accepted.");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private async Task<object?> Value(string sql, params object[] parameters)
    {
        await using var query = database.DataSource.CreateCommand(sql);
        foreach (var value in parameters) query.Parameters.AddWithValue(value);
        return await query.ExecuteScalarAsync(Token);
    }

    private static Task<(int Exit, string Output, string Error)> Process(string[] arguments, string? token) =>
        External("dotnet", [typeof(Wf.Cli.Program).Assembly.Location, .. arguments], Repository, token);

    private static async Task<(int Exit, string Output, string Error)> External(string executable,
        string[] arguments, string directory, string? token, Dictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = directory
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        start.Environment["WarningsNotAsErrors"] = "NU1902;NU1903";
        // Fix this explicit build input while independent workers commit on the shared branch.
        start.Environment["SourceRevisionId"] = new string('a', 40);
        if (token is null) start.Environment.Remove("WF_ACCESS_TOKEN");
        else start.Environment["WF_ACCESS_TOKEN"] = token;
        foreach (var item in environment ?? []) start.Environment[item.Key] = item.Value;
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(Token);
        var error = process.StandardError.ReadToEndAsync(Token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(TimeSpan.FromMinutes(4));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        return (process.ExitCode, await output, await error);
    }

    private static string Repository
    {
        get
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
                if (Directory.Exists(Path.Combine(directory.FullName, "backend", "proto"))) return directory.FullName;
            throw new DirectoryNotFoundException("Publish integration requires the repository source tree.");
        }
    }

    private sealed class PublishServer(WebApplication app, RSA rsa, RsaSecurityKey key, string origin) : IAsyncDisposable
    {
        private const string Tenant = "ef8a05a0-451c-4479-9c8d-391b6bc7ee1a";
        private const string Audience = "562b0870-4522-43da-8587-41e9b788f309";
        private const string Issuer = $"https://login.microsoftonline.com/{Tenant}/v2.0";
        internal static readonly Guid Actor = Guid.Parse("44cd65e3-f210-4201-b5a8-74321260704a");
        internal string Origin => origin;
        internal IBlobStore Blobs => app.Services.GetRequiredService<IBlobStore>();

        internal static async Task<PublishServer> Start(NpgsqlDataSource data, string blobRoot)
        {
            var rsa = RSA.Create(2048);
            var key = new RsaSecurityKey(rsa) { KeyId = "publish-integration" };
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Configuration["AzureAd:TenantId"] = Tenant;
            builder.Configuration["AzureAd:ClientId"] = Audience;
            builder.Configuration["Storage:UploadHmacKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            builder.Configuration["Storage:Root"] = blobRoot;
            builder.Services.AddWorkflowAuthentication(builder.Configuration, builder.Environment);
            builder.Services.AddTeamAuthorization();
            builder.Services.AddSingleton(data);
            builder.Services.AddWorkflowRegistrations(builder.Configuration);
            var metadata = new OpenIdConnectConfiguration { Issuer = Issuer };
            metadata.SigningKeys.Add(key);
            var manager = new StaticConfigurationManager<OpenIdConnectConfiguration>(metadata);
            builder.Services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.Configuration = metadata;
                options.ConfigurationManager = manager;
                options.TokenValidationParameters.IssuerValidator =
                    AadIssuerValidator.GetAadIssuerValidator(Issuer, null, _ => manager).Validate;
                options.TokenValidationParameters.ClockSkew = TimeSpan.Zero;
            });
            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapRegistrationEndpoints();
            app.MapVersionsEndpoints();
            await app.StartAsync(Token);
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return new(app, rsa, key, address);
        }

        internal string AccessToken(string role = "Registrar")
        {
            Claim[] claims = [new("tid", Tenant), new("ver", "2.0"), new("roles", role), new("idtyp", "app"),
                new("oid", Actor.ToString("D"))];
            return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(Issuer, Audience, claims,
                DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(20),
                new SigningCredentials(key, SecurityAlgorithms.RsaSha256)));
        }

        public async ValueTask DisposeAsync()
        {
            await app.DisposeAsync();
            rsa.Dispose();
        }
    }
}
