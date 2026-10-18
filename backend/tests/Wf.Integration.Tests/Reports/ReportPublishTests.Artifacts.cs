using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Wf.Agent;
using Wf.Agent.Hosts;
using Wf.Agent.Packages;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Artifacts;
using Wf.ControlPlane.Artifacts;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.Engine.Core.Placement;
using Wf.Engine.Core.Results;
using Wf.Sdk;

namespace Wf.Integration.Tests.Reports;

public sealed partial class ReportPublishTests
{
    private static readonly byte[] ArtifactTestKey = RandomNumberGenerator.GetBytes(32);

    [Fact]
    public async Task Artifact_actual_OpenAPI_matches_required_camelcase_metadata_receipts_and_raw_binary_upload()
    {
        await using var fixture = await ArtifactFixture();
        using var response = await fixture.Get("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        var paths = document.RootElement.GetProperty("paths");
        var upload = paths.GetProperty(ArtifactMetadata.PublishPath + "/{id}").GetProperty("put");
        var body = upload.GetProperty("requestBody").GetProperty("content").GetProperty("application/octet-stream")
            .GetProperty("schema");
        Assert.Equal("string", body.GetProperty("type").GetString());
        Assert.Equal("binary", body.GetProperty("format").GetString());
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        Assert.Equal(["contentType", "name", "sha256", "sizeBytes"],
            schemas.GetProperty("ArtifactMetadata").GetProperty("required").EnumerateArray()
                .Select(value => value.GetString()).Order());
        Assert.Equal(["artifactId", "runId"],
            schemas.GetProperty("ArtifactReceipt").GetProperty("required").EnumerateArray()
                .Select(value => value.GetString()).Order());
        Assert.True(paths.TryGetProperty("/api/v1/runs/{id}/artifacts", out _));
        var download = paths.GetProperty("/api/v1/artifacts/{id}/content").GetProperty("get")
            .GetProperty("responses").GetProperty("200").GetProperty("content")
            .GetProperty("application/octet-stream").GetProperty("schema");
        Assert.Equal("string", download.GetProperty("type").GetString());
        Assert.Equal("binary", download.GetProperty("format").GetString());
    }

    [Theory]
    [InlineData("missing-size", 400)]
    [InlineData("unknown", 400)]
    [InlineData("traversal", 400)]
    [InlineData("hash", 400)]
    [InlineData("negative", 400)]
    [InlineData("oversized", 413)]
    [InlineData("user-token", 403)]
    public async Task Artifact_HTTP_rejects_invalid_bounded_metadata_or_user_token_before_reserving(string scenario, int status)
    {
        await using var fixture = await ArtifactFixture();
        var attempt = await fixture.Seed();
        var capability = await ArtifactCapability(fixture.Host, attempt);
        var metadata = ArtifactMetadataFor(Bytes);
        var json = scenario switch
        {
            "missing-size" => $$"""{"name":"response.bin","contentType":"application/octet-stream","sha256":"{{metadata.Sha256}}"}""",
            "unknown" => JsonSerializer.Serialize(metadata, ArtifactJsonContext.Default.ArtifactMetadata)[..^1] + ""","unknown":true}""",
            "traversal" => JsonSerializer.Serialize(metadata with { Name = "../secret" }, ArtifactJsonContext.Default.ArtifactMetadata),
            "hash" => JsonSerializer.Serialize(metadata with { Sha256 = "a" }, ArtifactJsonContext.Default.ArtifactMetadata),
            "negative" => JsonSerializer.Serialize(metadata with { SizeBytes = -1 }, ArtifactJsonContext.Default.ArtifactMetadata),
            "oversized" => new string(' ', 4097) + "{}",
            _ => JsonSerializer.Serialize(metadata, ArtifactJsonContext.Default.ArtifactMetadata)
        };
        using var response = await ArtifactRequest(fixture, scenario == "user-token" ? fixture.Credential() : capability,
            HttpMethod.Post, ArtifactMetadata.GrantPath, Guid.CreateVersion7(),
            new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(status, (int)response.StatusCode);
        await using var count = database.DataSource.CreateCommand("SELECT count(*) FROM artifact_grants WHERE run_id=$1");
        count.Parameters.AddWithValue(attempt.RunId);
        Assert.Equal(0L, await count.ExecuteScalarAsync(Token));
    }
    [Fact]
    public async Task Artifact_actual_gateway_dispatch_agent_and_SDK_child_publish_download_and_leave_report_flow_intact()
    {
        await using var fixture = await ArtifactFixture(allowLocalGateway: true);
        var host = fixture.Host;
        var archive = await Package();
        host.App.MapGet("/artifact-package.zip", () => Microsoft.AspNetCore.Http.Results.Bytes(archive, "application/zip"));
        var hash = Convert.ToHexStringLower(SHA256.HashData(archive));
        var id = Guid.NewGuid();
        var options = new AgentOptions([host.GatewayAddress], id.ToString("D"), "artifact-" + id.ToString("N"),
            host.Pool, 2, heartbeatInterval: TimeSpan.FromMilliseconds(50),
            resultJournalDirectory: Path.Combine(host.Root, "artifact-journal"));
        using var cache = new PackageCache(Path.Combine(host.Root, "artifact-packages"), NullLogger<PackageCache>.Instance);
        await using var hosts = new TaskHostPool(NullLogger<TaskHostPool>.Instance, options: options);
        using var outbox = new AgentOutbox();
        await using var commands = new TaskCommands(options, cache, hosts, outbox, TimeProvider.System, NullLogger<TaskCommands>.Instance);
        using var connection = new AgentConnection(options, new AgentTelemetry(TimeProvider.System, commands), commands,
            TimeProvider.System, NullLogger<AgentConnection>.Instance, outbox);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var running = connection.RunAsync(stop.Token);
        try
        {
            await Until(() => Task.FromResult(host.Registry.Find(id) is not null), running);
            var attempt = await host.Seed(dispatch: false, task: "artifact");
            var run = await host.Runs.LoadAsync(attempt.RunId, Token);
            using var dispatcher = new Dispatcher(database.DataSource, host.Runs, host.Registry, new PlacementEngine(), TimeProvider.System);
            var placed = await dispatcher.DispatchAsync(run,
                new(attempt.RunId, "$post", "artifact", 1, hash, new(new(pool: host.Pool))),
                Command(attempt, hash, new Uri(host.Address, "/artifact-package.zip")), Token);
            Assert.Equal(id, placed.Agent!.Id);
            await Until(async () => (await host.Runs.LoadAsync(attempt.RunId, Token)).Snapshot.Tasks[attempt.TaskKey]
                is Wf.Contracts.ExecutionStatus.Succeeded or Wf.Contracts.ExecutionStatus.Failed, running);
            var final = await host.Runs.LoadAsync(attempt.RunId, Token);
            await using var error = database.DataSource.CreateCommand("SELECT error FROM task_attempts WHERE run_id=$1");
            error.Parameters.AddWithValue(attempt.RunId);
            Assert.True(final.Snapshot.Tasks[attempt.TaskKey] == Wf.Contracts.ExecutionStatus.Succeeded,
                "Actual artifact SDK child failed: " + await error.ExecuteScalarAsync(Token));
            await BindViewer(fixture, attempt.RunId);
            using var list = await fixture.Get($"/api/v1/runs/{attempt.RunId:D}/artifacts");
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            var artifacts = await list.Content.ReadFromJsonAsync(ArtifactJsonContext.Default.ArtifactSummaryArray, Token);
            var artifact = Assert.Single(artifacts!);
            Assert.Equal("$post/artifact", artifact.SourceKey);
            Assert.Equal("response.bin", artifact.Name);
            using var download = await fixture.Get($"/api/v1/artifacts/{artifact.Id:D}/content");
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            Assert.Equal("Published by $post/artifact", await download.Content.ReadAsStringAsync(Token));
            Assert.Equal(1, await host.Count(attempt.RunId));
            Assert.Empty(Directory.GetFiles(host.Root, ".wf-artifact-*", SearchOption.AllDirectories));
        }
        finally
        {
            await stop.CancelAsync();
            await running.WaitAsync(TimeSpan.FromSeconds(10), Token);
        }
    }

    [Fact]
    public async Task Artifact_actual_HTTP_reserves_exact_decimal_5GB_then_rejects_plus_one_without_transferring_5GB()
    {
        await using var fixture = await ArtifactFixture();
        var attempt = await fixture.Seed();
        var capability = await ArtifactCapability(fixture.Host, attempt);
        var id = Guid.CreateVersion7();
        var exact = ArtifactMetadataFor(Bytes) with { SizeBytes = ArtifactMetadata.RunBudget };
        using (var grant = await Grant(fixture, capability, id, exact))
            Assert.Equal(HttpStatusCode.Created, grant.StatusCode);
        using (var repeat = await Grant(fixture, capability, id, exact))
            Assert.Equal(HttpStatusCode.Created, repeat.StatusCode);
        using (var overflow = await Grant(fixture, capability, Guid.CreateVersion7(), exact with { SizeBytes = 1 }))
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, overflow.StatusCode);
        using (var singleOversized = await Grant(fixture, capability, Guid.CreateVersion7(),
            exact with { SizeBytes = ArtifactMetadata.RunBudget + 1 }))
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, singleOversized.StatusCode);
        using (var conflict = await Grant(fixture, capability, id, exact with { Name = "different.bin" }))
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        using (var release = await ArtifactRequest(fixture, capability, HttpMethod.Delete, ArtifactMetadata.GrantPath, id))
            Assert.Equal(HttpStatusCode.NoContent, release.StatusCode);
        using (var available = await Grant(fixture, capability, Guid.CreateVersion7(), exact))
            Assert.Equal(HttpStatusCode.Created, available.StatusCode);
        Assert.Equal(0, await ArtifactCount(attempt.RunId));
    }

    [Fact]
    public async Task Artifact_independent_server_instances_reserve_atomically_and_expired_grants_are_reclaimed()
    {
        var clock = new ArtifactClock();
        await using var first = await ArtifactFixture(clock);
        await using var second = await ArtifactFixture(clock);
        var attempt = await first.Seed();
        var capability = await ArtifactCapability(first.Host, attempt, TimeSpan.FromHours(1));
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(async index =>
        {
            using var response = await Grant(index % 2 == 0 ? first : second, capability, Guid.CreateVersion7(),
                ArtifactMetadataFor(Bytes) with { SizeBytes = 1_000_000_000 });
            return response.StatusCode;
        }));
        Assert.Equal(5, results.Count(result => result == HttpStatusCode.Created));
        Assert.Equal(7, results.Count(result => result == HttpStatusCode.RequestEntityTooLarge));
        clock.Advance(TimeSpan.FromMinutes(15).Add(TimeSpan.FromSeconds(1)));
        using var recovered = await Grant(first, capability, Guid.CreateVersion7(),
            ArtifactMetadataFor(Bytes) with { SizeBytes = ArtifactMetadata.RunBudget });
        Assert.Equal(HttpStatusCode.Created, recovered.StatusCode);
    }

    [Fact]
    public async Task Artifact_HTTP_commit_has_SHA_pointer_only_idempotent_retry_and_fresh_download_RBAC()
    {
        await using var fixture = await ArtifactFixture();
        var attempt = await fixture.Seed();
        var capability = await ArtifactCapability(fixture.Host, attempt);
        var id = Guid.CreateVersion7();
        using (var grant = await Grant(fixture, capability, id, ArtifactMetadataFor(Bytes)))
            Assert.Equal(HttpStatusCode.Created, grant.StatusCode);
        using (var upload = await Upload(fixture, capability, id, Bytes))
            Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        using (var duplicate = await Upload(fixture, capability, id, Bytes))
            Assert.Equal(HttpStatusCode.Created, duplicate.StatusCode);
        await fixture.Execute("UPDATE task_attempts SET status=4 WHERE run_id=$1", attempt.RunId);
        await fixture.Execute("UPDATE runs SET status=3 WHERE id=$1", attempt.RunId);
        using (var completed = await Grant(fixture, capability, id, ArtifactMetadataFor(Bytes)))
        {
            Assert.Equal(HttpStatusCode.Created, completed.StatusCode);
            Assert.True((await completed.Content.ReadFromJsonAsync(ArtifactJsonContext.Default.ArtifactGrant, Token))!.Committed);
        }
        using (var replay = await Upload(fixture, capability, id, Bytes))
            Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        using (var denied = await Grant(fixture, capability, Guid.CreateVersion7(), ArtifactMetadataFor(Bytes)))
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(1, await ArtifactCount(attempt.RunId));
        await using (var query = database.DataSource.CreateCommand(
            "SELECT source_key,size_bytes,blob_uri,encode(sha256,'hex'),row_to_json(artifacts)::text FROM artifacts WHERE id=$1"))
        {
            query.Parameters.AddWithValue(id);
            await using var row = await query.ExecuteReaderAsync(Token);
            Assert.True(await row.ReadAsync(Token));
            Assert.Equal(attempt.TaskKey, row.GetString(0));
            Assert.Equal(Bytes.Length, row.GetInt64(1));
            Assert.Equal("artifacts/" + Convert.ToHexStringLower(SHA256.HashData(Bytes)), row.GetString(2));
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Bytes)), row.GetString(3));
            Assert.DoesNotContain(Encoding.UTF8.GetString(Bytes), row.GetString(4), StringComparison.Ordinal);
        }
        var route = $"/api/v1/artifacts/{id:D}/content";
        using (var read = await fixture.Get(route, completion: HttpCompletionOption.ResponseHeadersRead))
        {
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            Assert.Null(read.Content.Headers.ContentLength);
            Assert.Equal(Bytes, await read.Content.ReadAsByteArrayAsync(Token));
            Assert.StartsWith("attachment;", read.Content.Headers.ContentDisposition!.ToString());
            Assert.Equal("application/octet-stream", read.Content.Headers.ContentType!.MediaType);
            Assert.Equal("nosniff", Assert.Single(read.Headers.GetValues("X-Content-Type-Options")));
        }
        using (var anonymous = await fixture.Get(route, anonymous: true))
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using (var otherUser = await fixture.Get(route, fixture.Credential(Guid.NewGuid())))
            Assert.Equal(HttpStatusCode.Forbidden, otherUser.StatusCode);
        using (var attemptToken = await fixture.Get(route, capability))
            Assert.Equal(HttpStatusCode.Unauthorized, attemptToken.StatusCode);
        await fixture.Execute("DELETE FROM role_bindings WHERE principal_id=$1", fixture.User);
        using var revoked = await fixture.Get(route);
        Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
    }

    [Theory]
    [InlineData("text/html", "result.html")]
    [InlineData("image/svg+xml", "result.svg")]
    public async Task Artifact_active_content_is_always_downloaded_not_rendered_as_a_report(string mime, string name)
    {
        await using var fixture = await ArtifactFixture();
        var attempt = await fixture.Seed();
        var capability = await ArtifactCapability(fixture.Host, attempt);
        var id = Guid.CreateVersion7();
        using (var grant = await Grant(fixture, capability, id, ArtifactMetadataFor(Bytes) with { Name = name, ContentType = mime }))
            Assert.Equal(HttpStatusCode.Created, grant.StatusCode);
        using (var published = await Upload(fixture, capability, id, Bytes))
            Assert.Equal(HttpStatusCode.Created, published.StatusCode);
        using var downloaded = await fixture.Get($"/api/v1/artifacts/{id:D}/content");
        Assert.Equal(HttpStatusCode.OK, downloaded.StatusCode);
        Assert.Equal("application/octet-stream", downloaded.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("attachment;", downloaded.Content.Headers.ContentDisposition!.ToString());
        Assert.Contains("sandbox;", Assert.Single(downloaded.Headers.GetValues("Content-Security-Policy")));
        Assert.Equal(Bytes, await downloaded.Content.ReadAsByteArrayAsync(Token));
    }

    [Theory]
    [InlineData("tampered")]
    [InlineData("expired")]
    [InlineData("agent")]
    [InlineData("session")]
    [InlineData("epoch")]
    [InlineData("attempt")]
    [InlineData("task")]
    [InlineData("terminal")]
    public async Task Artifact_HTTP_rejects_untrusted_or_stale_actor_without_reservation(string scenario)
    {
        var clock = new ArtifactClock();
        await using var fixture = await ArtifactFixture(clock);
        var attempt = await fixture.Seed();
        var capability = await ArtifactCapability(fixture.Host, attempt);
        var capabilities = fixture.Host.App.Services.GetRequiredService<ArtifactUploadCapabilities>();
        var actor = capabilities.Read(capability);
        if (scenario == "tampered") capability = (capability[0] == 'A' ? "B" : "A") + capability[1..];
        if (scenario == "expired") clock.Advance(TimeSpan.FromHours(1));
        if (scenario == "agent")
            await fixture.Execute("UPDATE task_attempts SET agent_id=$2 WHERE run_id=$1", attempt.RunId, Guid.NewGuid());
        if (scenario == "session")
            await fixture.Execute("UPDATE agents SET session_id=$2 WHERE id=$1", actor.AgentId, Guid.NewGuid());
        if (scenario is "epoch" or "attempt" or "task")
        {
            await using var query = database.DataSource.CreateCommand("SELECT dispatch_payload FROM task_attempts WHERE run_id=$1");
            query.Parameters.AddWithValue(attempt.RunId);
            var dispatch = Dispatch.Parser.ParseFrom((byte[])(await query.ExecuteScalarAsync(Token))!);
            if (scenario == "epoch") dispatch.ReservationEpoch++;
            if (scenario == "attempt") dispatch.Task.Attempt++;
            if (scenario == "task") dispatch.Task.TaskKey = "not-declared";
            capability = capabilities.Create(dispatch, actor.AgentId, actor.SessionId).BearerToken;
        }
        if (scenario == "terminal")
            await fixture.Execute("UPDATE task_attempts SET status=4 WHERE run_id=$1", attempt.RunId);
        using var response = await Grant(fixture, capability, Guid.CreateVersion7(), ArtifactMetadataFor(Bytes));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await ArtifactCount(attempt.RunId));
        await using var count = database.DataSource.CreateCommand("SELECT count(*) FROM artifact_grants WHERE run_id=$1");
        count.Parameters.AddWithValue(attempt.RunId);
        Assert.Equal(0L, await count.ExecuteScalarAsync(Token));
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("short")]
    [InlineData("long")]
    public async Task Artifact_corrupt_or_incorrect_length_upload_never_commits_and_can_retry(string scenario)
    {
        await using var fixture = await ArtifactFixture();
        var attempt = await fixture.Seed();
        var capability = await ArtifactCapability(fixture.Host, attempt);
        var id = Guid.CreateVersion7();
        using (var grant = await Grant(fixture, capability, id, ArtifactMetadataFor(Bytes)))
            Assert.Equal(HttpStatusCode.Created, grant.StatusCode);
        var corrupt = scenario switch
        {
            "short" => Bytes[..^1],
            "long" => Bytes.Concat(new byte[] { 0 }).ToArray(),
            _ => new byte[Bytes.Length]
        };
        using (var rejected = await Upload(fixture, capability, id, corrupt))
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal(0, await ArtifactCount(attempt.RunId));
        Assert.Empty(Directory.GetFiles(fixture.Host.Root, ".upload-*", SearchOption.AllDirectories));
        using var retry = await Upload(fixture, capability, id, Bytes);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal(1, await ArtifactCount(attempt.RunId));
    }

    [Fact]
    public async Task Artifact_active_upload_keeps_expired_reservation_counted_until_commit()
    {
        var clock = new ArtifactClock();
        await using var fixture = await ArtifactFixture(clock);
        var attempt = await fixture.Seed();
        var capability = await ArtifactCapability(fixture.Host, attempt, TimeSpan.FromHours(1));
        var actor = fixture.Host.App.Services.GetRequiredService<ArtifactUploadCapabilities>().Read(capability);
        var service = fixture.Host.App.Services.GetRequiredService<ArtifactGrantService>();
        var id = Guid.CreateVersion7();
        await service.GrantAsync(actor, id, ArtifactMetadataFor(Bytes), Token);
        await service.GrantAsync(actor, Guid.CreateVersion7(),
            ArtifactMetadataFor(Bytes) with { SizeBytes = ArtifactMetadata.RunBudget - Bytes.Length }, Token);
        using var input = new PausedArtifactStream(Bytes);
        var upload = service.PublishAsync(actor, id, input, Bytes.Length, Token);
        await input.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        clock.Advance(TimeSpan.FromMinutes(15).Add(TimeSpan.FromSeconds(1)));
        var grant = service.GrantAsync(actor, Guid.CreateVersion7(),
            ArtifactMetadataFor(Bytes) with { SizeBytes = ArtifactMetadata.RunBudget }, Token);
        try
        {
            await Task.Delay(150, Token);
            Assert.False(grant.IsCompleted);
        }
        finally { input.Resume.TrySetResult(); }
        await upload;
        await Assert.ThrowsAsync<ArtifactLimitExceededException>(() => grant);
        Assert.Equal(1, await ArtifactCount(attempt.RunId));
    }

    [Fact]
    public async Task Artifact_expired_upload_cannot_commit_and_other_attempt_cannot_read_upload_or_release_grant()
    {
        var clock = new ArtifactClock();
        await using var fixture = await ArtifactFixture(clock);
        var attempt = await fixture.Seed();
        var capability = await ArtifactCapability(fixture.Host, attempt, TimeSpan.FromHours(1));
        var id = Guid.CreateVersion7();
        using (var granted = await Grant(fixture, capability, id, ArtifactMetadataFor(Bytes)))
            Assert.Equal(HttpStatusCode.Created, granted.StatusCode);
        var other = await fixture.Seed();
        var foreign = await ArtifactCapability(fixture.Host, other, TimeSpan.FromHours(1));
        using (var upload = await Upload(fixture, foreign, id, Bytes))
            Assert.Equal(HttpStatusCode.Forbidden, upload.StatusCode);
        using (var release = await ArtifactRequest(fixture, foreign, HttpMethod.Delete, ArtifactMetadata.GrantPath, id))
            Assert.Equal(HttpStatusCode.Forbidden, release.StatusCode);
        clock.Advance(TimeSpan.FromMinutes(15).Add(TimeSpan.FromSeconds(1)));
        using var expired = await Upload(fixture, capability, id, Bytes);
        Assert.Equal(HttpStatusCode.Forbidden, expired.StatusCode);
        Assert.Equal(0, await ArtifactCount(attempt.RunId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Artifact_upload_cancellation_or_midstream_session_fencing_never_commits_and_reservation_can_release(bool fencing)
    {
        await using var fixture = await ArtifactFixture();
        var attempt = await fixture.Seed();
        var capability = await ArtifactCapability(fixture.Host, attempt);
        var actor = fixture.Host.App.Services.GetRequiredService<ArtifactUploadCapabilities>().Read(capability);
        var service = fixture.Host.App.Services.GetRequiredService<ArtifactGrantService>();
        var id = Guid.CreateVersion7();
        await service.GrantAsync(actor, id, ArtifactMetadataFor(Bytes), Token);
        using var input = new PausedArtifactStream(Bytes);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var uploading = service.PublishAsync(actor, id, input, Bytes.Length, cancel.Token);
        await input.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        if (fencing)
        {
            await fixture.Execute("UPDATE agents SET session_id=$2 WHERE id=$1", actor.AgentId, Guid.NewGuid());
            input.Resume.TrySetResult();
            await Assert.ThrowsAsync<ArtifactAttemptRejectedException>(() => uploading);
        }
        else
        {
            await cancel.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => uploading);
        }
        Assert.Equal(0, await ArtifactCount(attempt.RunId));
        Assert.Empty(Directory.GetFiles(fixture.Host.Root, ".upload-*", SearchOption.AllDirectories));
        await service.ReleaseAsync(actor, id, Token);
        await using var count = database.DataSource.CreateCommand("SELECT count(*) FROM artifact_grants WHERE run_id=$1");
        count.Parameters.AddWithValue(attempt.RunId);
        Assert.Equal(0L, await count.ExecuteScalarAsync(Token));
    }

    private Task<ReportReadFixture> ArtifactFixture(TimeProvider? clock = null, byte[]? key = null, bool allowLocalGateway = false) =>
        ReportReadFixture.Start(database.DataSource, services: builder =>
        {
            // The loopback agent fixture predates agent credentials. Viewer routes still
            // use explicit RequireAuthorization and the real JWT/team authorization stack.
            if (allowLocalGateway)
                builder.Services.PostConfigure<AuthorizationOptions>(options => options.FallbackPolicy = null);
            builder.Services.AddSingleton(clock ?? TimeProvider.System);
            builder.Services.AddSingleton(provider => new ArtifactUploadCapabilities(
                key ?? ArtifactTestKey,
                provider.GetRequiredService<TimeProvider>()));
            builder.Services.AddSingleton<ArtifactGrantService>();
        }, configure: app => app.MapArtifactEndpoints());

    private async Task<string> ArtifactCapability(ReportHost host, ResultIdentity attempt, TimeSpan? lifetime = null)
    {
        await using var query = database.DataSource.CreateCommand("""
            SELECT a.dispatch_payload,a.agent_id,g.session_id FROM task_attempts a JOIN agents g ON g.id=a.agent_id
            WHERE a.run_id=$1 AND a.task_key=$2 AND a.attempt=$3
            """);
        query.Parameters.AddWithValue(attempt.RunId);
        query.Parameters.AddWithValue(attempt.TaskKey);
        query.Parameters.AddWithValue(attempt.Attempt);
        await using var row = await query.ExecuteReaderAsync(Token);
        Assert.True(await row.ReadAsync(Token));
        var dispatch = Dispatch.Parser.ParseFrom(row.GetFieldValue<byte[]>(0));
        if (lifetime is { } duration) dispatch.DeadlineAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.Add(duration));
        return host.App.Services.GetRequiredService<ArtifactUploadCapabilities>()
            .Create(dispatch, row.GetGuid(1), row.GetGuid(2)).BearerToken;
    }

    private static ArtifactMetadata ArtifactMetadataFor(byte[] bytes) =>
        new("response.bin", "application/octet-stream", bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));

    private static Task<HttpResponseMessage> Grant(ReportReadFixture fixture, string capability, Guid id, ArtifactMetadata metadata) =>
        ArtifactRequest(fixture, capability, HttpMethod.Post, ArtifactMetadata.GrantPath, id,
            JsonContent.Create(metadata, ArtifactJsonContext.Default.ArtifactMetadata));

    private static Task<HttpResponseMessage> Upload(ReportReadFixture fixture, string capability, Guid id, byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new("application/octet-stream");
        return ArtifactRequest(fixture, capability, HttpMethod.Put, ArtifactMetadata.PublishPath, id, content);
    }

    private static async Task<HttpResponseMessage> ArtifactRequest(ReportReadFixture fixture, string capability,
        HttpMethod method, string path, Guid id, HttpContent? body = null)
    {
        using var request = new HttpRequestMessage(method, path + "/" + id.ToString("D")) { Content = body };
        request.Headers.Authorization = new("Bearer", capability);
        return await fixture.Host.Client.SendAsync(request, Token);
    }

    private async Task<long> ArtifactCount(Guid runId)
    {
        await using var count = database.DataSource.CreateCommand("SELECT count(*) FROM artifacts WHERE run_id=$1");
        count.Parameters.AddWithValue(runId);
        return (long)(await count.ExecuteScalarAsync(Token))!;
    }

    private static Task BindViewer(ReportReadFixture fixture, Guid runId) => fixture.Execute("""
        INSERT INTO role_bindings(id,scope_kind,scope_id,role,principal_id,principal_kind)
        SELECT $2,2,split_part(workflow_id,'/',1)||'/'||split_part(workflow_id,'/',2),0,$3,1
        FROM runs WHERE run_day=uuid_v7_run_day($1) AND id=$1
        """, runId, Guid.CreateVersion7(), fixture.User);

    private sealed class ArtifactClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }

    private sealed class PausedArtifactStream(byte[] bytes) : MemoryStream(bytes)
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Resume.Task.WaitAsync(cancellationToken);
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }
}
