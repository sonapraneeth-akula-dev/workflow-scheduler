using System.Net;
using System.Security.Cryptography;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Checkpoints;
using Wf.ControlPlane.Engine.Agents;
using Wf.ControlPlane.Engine.Checkpoints;
using Wf.ControlPlane.Reports;

namespace Wf.Integration.Tests.Checkpoints;

public sealed partial class CheckpointApplierTests
{
    [Fact]
    public async Task Checkpoint_blob_only_HTTP_upload_is_idempotent_and_cannot_publish_the_index_before_agent_ACK()
    {
        await using var fixture = await Fixture.Create(database);
        var saved = await fixture.Upload("{}");
        File.Delete(fixture.PathFor(saved));
        using var capabilities = new CheckpointUploadCapabilities(RandomNumberGenerator.GetBytes(32), fixture.Runtime.Clock);
        var grant = capabilities.Create(fixture.Dispatch, fixture.Runtime.Session);
        await using var app = await UploadHost(fixture, capabilities);
        using var http = UploadClient(app);
        for (var replay = 0; replay < 2; replay++)
        {
            using var request = UploadRequest(saved, "{}"u8.ToArray(), grant.BearerToken);
            using var response = await http.SendAsync(request, Token);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
            Assert.Equal(0L, await fixture.Runtime.Scalar("SELECT count(*) FROM task_checkpoints WHERE run_id=$1"));
            Assert.True(File.Exists(fixture.PathFor(saved)));
        }
        await fixture.Accept(saved);
        Assert.Equal(1L, await fixture.Runtime.Scalar("SELECT sequence FROM task_checkpoints WHERE run_id=$1"));
    }

    [Theory]
    [InlineData("missing", 403)]
    [InlineData("tampered", 403)]
    [InlineData("purpose", 403)]
    [InlineData("expired", 403)]
    [InlineData("task", 403)]
    [InlineData("session", 403)]
    [InlineData("fence", 403)]
    [InlineData("terminal", 403)]
    [InlineData("digest", 400)]
    [InlineData("json", 400)]
    [InlineData("size", 400)]
    [InlineData("length", 413)]
    [InlineData("media-type", 415)]
    [InlineData("header", 400)]
    public async Task Checkpoint_HTTP_boundary_rejects_invalid_purpose_identity_fences_bytes_and_lengths_without_persistence(
        string invalid, int expectedStatus)
    {
        await using var fixture = await Fixture.Create(database);
        var saved = await fixture.Upload("{}");
        File.Delete(fixture.PathFor(saved));
        var key = RandomNumberGenerator.GetBytes(32);
        using var issuer = new CheckpointUploadCapabilities(key, fixture.Runtime.Clock);
        using var capabilities = new CheckpointUploadCapabilities(key,
            invalid == "expired" ? new FixedClock(fixture.Runtime.Clock.Now.AddDays(2)) : fixture.Runtime.Clock);
        var grant = issuer.Create(fixture.Dispatch, fixture.Runtime.Session);
        var token = grant.BearerToken;
        var bytes = "{}"u8.ToArray();
        switch (invalid)
        {
            case "missing": token = ""; break;
            case "tampered": token = (token[0] == 'a' ? 'b' : 'a') + token[1..]; break;
            case "purpose":
                using (var reports = new ReportUploadCapabilities(key, fixture.Runtime.Clock))
                    token = reports.Create(fixture.Dispatch.Task, fixture.Dispatch.DeadlineAt.ToDateTimeOffset()).BearerToken;
                break;
            case "task":
                saved.Task.TaskKey = "foreign";
                saved.BlobKey = CheckpointProtocol.Key(fixture.Runtime.RunId, "work/foreign", 1, saved.Sha256);
                break;
            case "session":
                using (var registry = new AgentRegistry(database.DataSource, fixture.Runtime.Clock))
                    await registry.RegisterAsync(fixture.Hello(), Token);
                break;
            case "fence":
                await fixture.Runtime.Execute("UPDATE agents SET reservation_epoch=reservation_epoch+1 WHERE id=$1",
                    fixture.Runtime.Session.AgentId);
                break;
            case "terminal":
                await fixture.Runtime.Complete(fixture.Dispatch);
                await fixture.Runtime.Finish();
                break;
            case "digest": bytes = "[]"u8.ToArray(); break;
            case "json":
                bytes = "}{"u8.ToArray();
                saved.Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
                saved.BlobKey = CheckpointProtocol.Key(fixture.Runtime.RunId, "work/task", 1, saved.Sha256);
                break;
            case "size": saved.SizeBytes = CheckpointProtocol.MaximumBytes + 1UL; break;
            case "length": saved.SizeBytes++; break;
        }
        await using var app = await UploadHost(fixture, capabilities);
        using var http = UploadClient(app);
        using var request = UploadRequest(saved, bytes, token);
        if (invalid == "media-type") request.Content!.Headers.ContentType = new("application/octet-stream");
        if (invalid == "header") request.Headers.Add(CheckpointProtocol.MetadataHeader, "duplicate");
        using var response = await http.SendAsync(request, Token);
        Assert.Equal(expectedStatus, (int)response.StatusCode);
        Assert.Equal(0L, await fixture.Runtime.Scalar("SELECT count(*) FROM task_checkpoints WHERE run_id=$1"));
        Assert.False(File.Exists(fixture.PathFor(saved)));
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("missing")]
    [InlineData("corrupt")]
    public async Task Checkpoint_resume_reader_reconstructs_and_verifies_the_immutable_blob_and_never_returns_partial_state(string kind)
    {
        await using var fixture = await Fixture.Create(database);
        Assert.Null(await fixture.Applier.LoadAsync(fixture.Dispatch.Task, Token));
        var target = fixture.Dispatch.Task.Clone();
        target.Attempt = 2;
        Assert.Null(await fixture.Applier.LoadAsync(target, Token));
        var saved = await fixture.Upload("""{"position":42}""");
        saved.SavedAt = Timestamp.FromDateTimeOffset(CheckpointProtocol.Validate(saved).SavedAt);
        await fixture.Accept(saved);
        if (kind == "missing")
        {
            File.Delete(fixture.PathFor(saved));
            await Assert.ThrowsAsync<FileNotFoundException>(() => fixture.Applier.LoadAsync(target, Token));
        }
        else if (kind == "corrupt")
        {
            await File.WriteAllTextAsync(fixture.PathFor(saved), """{"position":99}""", Token);
            await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Applier.LoadAsync(target, Token));
        }
        else
        {
            var seed = await fixture.Applier.LoadAsync(target, Token);
            Assert.NotNull(seed);
            Assert.Equal(saved, seed.Reference);
            Assert.Equal("""{"position":42}""", seed.StateJson.ToStringUtf8());
            Assert.Equal(1, CheckpointProtocol.ValidateSeed(seed, target).Sequence);
        }
        Assert.Equal(1L, await fixture.Runtime.Scalar("SELECT sequence FROM task_checkpoints WHERE run_id=$1"));
    }

    private static HttpRequestMessage UploadRequest(CheckpointSaved saved, byte[] bytes, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, CheckpointProtocol.EndpointPath)
        {
            Content = new ByteArrayContent(bytes)
        };
        request.Content.Headers.ContentType = new("application/json");
        if (token.Length != 0) request.Headers.Authorization = new("Bearer", token);
        request.Headers.Add(CheckpointProtocol.MetadataHeader, WebEncoders.Base64UrlEncode(saved.ToByteArray()));
        return request;
    }

    private static HttpClient UploadClient(WebApplication app) => new()
    {
        BaseAddress = new(app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.Single())
    };

    private static async Task<WebApplication> UploadHost(Fixture fixture, CheckpointUploadCapabilities capabilities)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddSingleton(capabilities);
        builder.Services.AddSingleton(fixture.Applier);
        var app = builder.Build();
        app.MapCheckpointUploadEndpoints();
        await app.StartAsync(Token);
        return app;
    }
}
