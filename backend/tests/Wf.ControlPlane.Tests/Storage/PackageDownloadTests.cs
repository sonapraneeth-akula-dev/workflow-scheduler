using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Wf.ControlPlane.Storage;

namespace Wf.ControlPlane.Tests.Storage;

public sealed class PackageDownloadTests
{
    [Theory]
    [InlineData("valid", HttpStatusCode.OK)]
    [InlineData("upload-purpose", HttpStatusCode.Forbidden)]
    [InlineData("wrong-kind", HttpStatusCode.Forbidden)]
    [InlineData("expired", HttpStatusCode.Forbidden)]
    [InlineData("tampered", HttpStatusCode.Forbidden)]
    [InlineData("missing", HttpStatusCode.ServiceUnavailable)]
    public async Task Package_GET_is_integrity_checked_short_lived_and_purpose_separated(string fault, HttpStatusCode expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "wf-package-download-" + Guid.NewGuid().ToString("N"));
        var key = RandomNumberGenerator.GetBytes(32);
        var bytes = "immutable compiled package test bytes"u8.ToArray();
        var content = new BlobContent(fault == "wrong-kind" ? BlobKind.Sources : BlobKind.Packages,
            Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length);
        var time = new Clock();
        try
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton<TimeProvider>(time);
            builder.Services.AddPackageDownloads(key);
            builder.Services.AddSingleton<IBlobStore>(_ => new FileSystemBlobStore(root, key));
            await using var app = builder.Build();
            app.MapPackageDownloads();
            if (fault != "missing")
                await app.Services.GetRequiredService<IBlobStore>().UploadAsync(content, new MemoryStream(bytes),
                    TestContext.Current.CancellationToken);
            var grants = app.Services.GetRequiredKeyedService<BlobUploadGrants>(PackageDownloadEndpoints.Grants);
            var token = grants.Create(content, bytes.Length, TimeSpan.FromMinutes(10));
            if (fault == "upload-purpose")
            {
                using var uploads = new BlobUploadGrants(key, timeProvider: time);
                token = uploads.Create(content, bytes.Length, TimeSpan.FromMinutes(10));
            }
            if (fault == "tampered") token = "A" + token[1..];
            if (fault == "expired") time.Now = time.Now.AddMinutes(10);
            await app.StartAsync(TestContext.Current.CancellationToken);
            using var client = new HttpClient
            {
                BaseAddress = new Uri(Assert.Single(app.Services.GetRequiredService<IServer>()
                    .Features.Get<IServerAddressesFeature>()!.Addresses))
            };
            using var response = await client.GetAsync("/api/v1/packages/" + token, TestContext.Current.CancellationToken);
            Assert.Equal(expected, response.StatusCode);
            if (expected == HttpStatusCode.OK)
            {
                Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
                Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
                Assert.True(response.Headers.CacheControl?.NoStore);
            }
            await app.StopAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
