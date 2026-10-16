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
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Microsoft.IdentityModel.Validators;
using Npgsql;
using Wf.Contracts.Registry;
using Wf.ControlPlane.Auth;
using Wf.ControlPlane.Registration;
using Wf.ControlPlane.Storage;

namespace Wf.Integration.Tests.Registration;

public sealed class PublishRegistrationTests(RegistrationDatabase database) : IClassFixture<RegistrationDatabase>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

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

    private static async Task<(int Exit, string Output, string Error)> Process(string[] arguments, string? token)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            WorkingDirectory = Repository
        };
        start.ArgumentList.Add(typeof(Wf.Cli.Program).Assembly.Location);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        start.Environment["WarningsNotAsErrors"] = "NU1902;NU1903";
        // Fix this explicit build input while independent workers commit on the shared branch.
        start.Environment["SourceRevisionId"] = new string('a', 40);
        if (token is null) start.Environment.Remove("WF_ACCESS_TOKEN");
        else start.Environment["WF_ACCESS_TOKEN"] = token;
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
