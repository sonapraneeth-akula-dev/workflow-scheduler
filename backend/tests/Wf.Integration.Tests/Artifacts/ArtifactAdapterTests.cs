using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Artifacts;
using Wf.ControlPlane.Artifacts;
using Wf.ControlPlane.Storage;
using Wf.Integration.Tests.Reports;
using Wf.Integration.Tests.Storage;

namespace Wf.Integration.Tests.Artifacts;

public sealed class ArtifactAdapterTests(ReportPublishDatabase database, BlobStoreEnvironment environment)
    : IClassFixture<ReportPublishDatabase>, IClassFixture<BlobStoreEnvironment>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_HTTP_grant_commit_and_authenticated_download_reuse_verified_filesystem_and_Azure_adapters(bool azure)
    {
        await using var storage = await environment.CreateAsync(azure);
        using var capabilities = new ArtifactUploadCapabilities(RandomNumberGenerator.GetBytes(32), TimeProvider.System);
        await using var fixture = await ReportReadFixture.Start(database.DataSource, storage.Store,
            services: builder =>
            {
                builder.Services.AddSingleton(capabilities);
                builder.Services.AddSingleton<ArtifactGrantService>();
            }, configure: app => app.MapArtifactEndpoints());
        var attempt = await fixture.Seed();
        string capability;
        await using (var owner = database.DataSource.CreateCommand("""
            SELECT a.dispatch_payload,a.agent_id,g.session_id FROM task_attempts a JOIN agents g ON g.id=a.agent_id
            WHERE a.run_id=$1 AND a.task_key=$2 AND a.attempt=$3
            """))
        {
            owner.Parameters.AddWithValue(attempt.RunId);
            owner.Parameters.AddWithValue(attempt.TaskKey);
            owner.Parameters.AddWithValue(attempt.Attempt);
            await using var row = await owner.ExecuteReaderAsync(Token);
            Assert.True(await row.ReadAsync(Token));
            capability = capabilities.Create(Dispatch.Parser.ParseFrom(row.GetFieldValue<byte[]>(0)),
                row.GetGuid(1), row.GetGuid(2)).BearerToken;
        }
        var bytes = RandomNumberGenerator.GetBytes(300_123);
        var metadata = new ArtifactMetadata("response.bin", "application/octet-stream", bytes.Length,
            Convert.ToHexStringLower(SHA256.HashData(bytes)));
        var id = Guid.CreateVersion7();
        using (var grant = new HttpRequestMessage(HttpMethod.Post, ArtifactMetadata.GrantPath + "/" + id.ToString("D"))
        {
            Content = JsonContent.Create(metadata, ArtifactJsonContext.Default.ArtifactMetadata),
            Headers = { Authorization = new("Bearer", capability) }
        })
        using (var granted = await fixture.Host.Client.SendAsync(grant, Token))
            Assert.Equal(HttpStatusCode.Created, granted.StatusCode);
        using (var upload = new HttpRequestMessage(HttpMethod.Put, ArtifactMetadata.PublishPath + "/" + id.ToString("D"))
        {
            Content = new ByteArrayContent(bytes), Headers = { Authorization = new("Bearer", capability) }
        })
        {
            upload.Content.Headers.ContentType = new("application/octet-stream");
            using var committed = await fixture.Host.Client.SendAsync(upload, Token);
            Assert.Equal(HttpStatusCode.Created, committed.StatusCode);
        }
        using (var download = await fixture.Get($"/api/v1/artifacts/{id:D}/content"))
        {
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            Assert.Equal(bytes, await download.Content.ReadAsByteArrayAsync(Token));
        }
        var content = new BlobContent(BlobKind.Artifacts, metadata.Sha256, bytes.Length);
        Assert.True(await storage.ExistsAsync(content));
        await storage.AssertNoStagingAsync();
        await storage.CorruptAsync(content, new byte[bytes.Length]);
        var failed = await Record.ExceptionAsync(async () =>
        {
            using var download = await fixture.Get($"/api/v1/artifacts/{id:D}/content");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, download.StatusCode);
        });
        if (failed is not null) Assert.IsAssignableFrom<HttpRequestException>(failed);
    }
}
