using System.Collections.Immutable;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Wf.Agent;
using Wf.Agent.Hosts;
using Wf.Agent.Packages;
using Wf.Contracts;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Definition;
using Wf.Contracts.Events;
using Wf.Contracts.Reports;
using Wf.ControlPlane.Data;
using Wf.ControlPlane.Engine.Agents;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Reports;
using Wf.ControlPlane.Storage;
using Wf.Engine.Core;
using Wf.Engine.Core.Placement;
using Wf.Engine.Core.Results;
using Wf.Integration.Tests.Agents;
using Wf.TaskHost.Fixture;
using DispatchCommand = Wf.Contracts.Agent.V1.Dispatch;

namespace Wf.Integration.Tests.Reports;

public sealed partial class ReportPublishTests(ReportPublishDatabase database) : IClassFixture<ReportPublishDatabase>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly byte[] Bytes = Encoding.UTF8.GetBytes("report payload never belongs in PostgreSQL");

    [Fact]
    public async Task ReportPublish_actual_OpenAPI_declares_raw_binary_required_metadata_and_committed_identity()
    {
        await using var host = await ReportHost.Start(database.DataSource);
        using var response = await host.Client.GetAsync("/openapi/v1.json", Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        var operation = document.RootElement.GetProperty("paths")
            .GetProperty("/api/v1/report-publishes/{id}").GetProperty("post");
        var body = operation.GetProperty("requestBody").GetProperty("content")
            .GetProperty("application/octet-stream").GetProperty("schema");
        Assert.Equal("string", body.GetProperty("type").GetString());
        Assert.Equal("binary", body.GetProperty("format").GetString());
        var header = Assert.Single(operation.GetProperty("parameters").EnumerateArray(),
            parameter => parameter.GetProperty("name").GetString() == ReportUploadMetadata.HeaderName);
        Assert.True(header.GetProperty("required").GetBoolean());
        Assert.Equal("header", header.GetProperty("in").GetString());
        var receipt = document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty("ReportReceipt");
        Assert.Equal(["reportId", "runId"],
            receipt.GetProperty("required").EnumerateArray().Select(value => value.GetString()).Order());
    }

    [Fact]
    public async Task ReportPublish_actual_HTTP_stream_writes_only_pointer_and_emits_committed_ReportAdded_once()
    {
        await using var host = await ReportHost.Start(database.DataSource);
        var attempt = await host.Seed();
        await using var listener = await database.DataSource.OpenConnectionAsync(Token);
        var notifications = new List<string>();
        listener.Notification += (_, notification) => notifications.Add(notification.Payload);
        await using (var listen = new NpgsqlCommand("LISTEN report_added", listener))
            await listen.ExecuteNonQueryAsync(Token);
        var id = Guid.CreateVersion7();
        using var response = await host.Publish(attempt, id, Bytes);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var receipt = JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(Token),
            ReportJsonContext.Default.ReportReceipt)!;
        Assert.Equal(attempt.RunId, receipt.RunId);
        Assert.Equal(id, receipt.ReportId);
        await listener.WaitAsync(1000, Token);
        using var notification = JsonDocument.Parse(Assert.Single(notifications));
        Assert.Equal("ReportAdded", notification.RootElement.GetProperty("kind").GetString());
        Assert.Equal(id, notification.RootElement.GetProperty("reportId").GetGuid());
        Assert.Equal(attempt.RunId, notification.RootElement.GetProperty("runId").GetGuid());
        await using (var row = database.DataSource.CreateCommand("""
            SELECT source_key,attempt,kind,title,tags,size_bytes,blob_uri,encode(sha256,'hex'),row_to_json(reports)::text
            FROM reports WHERE run_id=$1 AND id=$2
            """))
        {
            row.Parameters.AddWithValue(attempt.RunId);
            row.Parameters.AddWithValue(id);
            await using var reader = await row.ExecuteReaderAsync(Token);
            Assert.True(await reader.ReadAsync(Token));
            Assert.Equal("$post/report", reader.GetString(0));
            Assert.Equal(1, reader.GetInt16(1));
            Assert.Equal("text", reader.GetString(2));
            Assert.Equal("Summary", reader.GetString(3));
            Assert.Equal(["review"], reader.GetFieldValue<string[]>(4));
            Assert.Equal(Bytes.Length, reader.GetInt64(5));
            Assert.Equal($"reports/{attempt.RunId:D}/{id:D}", reader.GetString(6));
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Bytes)), reader.GetString(7));
            Assert.DoesNotContain(Encoding.UTF8.GetString(Bytes), reader.GetString(8), StringComparison.Ordinal);
        }
        Assert.Equal(Bytes, await host.Read(attempt.RunId, id));
        using var duplicate = await host.Publish(attempt, id, Bytes);
        Assert.Equal(HttpStatusCode.Created, duplicate.StatusCode);
        Assert.False(await listener.WaitAsync(150, Token));
        Assert.Single(notifications);
        Assert.Equal(1, await host.Count(attempt.RunId));
        Assert.Empty(Directory.GetFiles(host.Root, ".upload-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ReportPublish_default_eleventh_and_ten_MB_plus_one_fail_without_blob_or_extra_row()
    {
        await using var host = await ReportHost.Start(database.DataSource);
        var attempt = await host.Seed();
        var large = new byte[10_000_001];
        var rejected = Guid.CreateVersion7();
        using (var response = await host.Publish(attempt, rejected, large))
        {
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
            Assert.Contains("10000000 bytes", await response.Content.ReadAsStringAsync(Token));
        }
        Assert.False(host.Exists(attempt.RunId, rejected));
        for (var index = 0; index < 10; index++)
        {
            using var accepted = await host.Publish(attempt, Guid.CreateVersion7(), Bytes);
            Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        }
        var eleventh = Guid.CreateVersion7();
        using var denied = await host.Publish(attempt, eleventh, Bytes);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, denied.StatusCode);
        Assert.Contains("10 reports", await denied.Content.ReadAsStringAsync(Token));
        Assert.Equal(10, await host.Count(attempt.RunId));
        Assert.False(host.Exists(attempt.RunId, eleventh));
    }

    [Fact]
    public async Task ReportPublish_exact_ten_MB_and_actual_type_overrides_are_authoritative()
    {
        await using var host = await ReportHost.Start(database.DataSource);
        var defaultAttempt = await host.Seed();
        var id = Guid.CreateVersion7();
        using (var exact = await host.Publish(defaultAttempt, id, new byte[10_000_000]))
            Assert.Equal(HttpStatusCode.Created, exact.StatusCode);
        Assert.Equal(10_000_000, (await host.Read(defaultAttempt.RunId, id)).Length);
        var custom = await host.Seed("""{"reports":{"maxCount":2,"maxBytes":3}}""");
        using (var tooLarge = await host.Publish(custom, Guid.CreateVersion7(), new byte[4]))
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
        for (var index = 0; index < 2; index++)
        {
            using var accepted = await host.Publish(custom, Guid.CreateVersion7(), new byte[3]);
            Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        }
        using var tooMany = await host.Publish(custom, Guid.CreateVersion7(), []);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooMany.StatusCode);
        Assert.Equal(2, await host.Count(custom.RunId));
    }

    [Fact]
    public async Task ReportPublish_concurrent_independent_server_instances_never_exceed_run_quota()
    {
        await using var first = await ReportHost.Start(database.DataSource);
        await using var second = await ReportHost.Start(database.DataSource);
        var attempt = await first.Seed("""{"reports":{"maxCount":3}}""");
        var attempts = Enumerable.Range(0, 12).Select(async index =>
        {
            var host = index % 2 == 0 ? first : second;
            using var response = await host.Publish(attempt, Guid.CreateVersion7(), Bytes);
            return response.StatusCode;
        });
        var statuses = await Task.WhenAll(attempts);
        Assert.Equal(3, statuses.Count(status => status == HttpStatusCode.Created));
        Assert.Equal(9, statuses.Count(status => status == HttpStatusCode.RequestEntityTooLarge));
        Assert.Equal(3, await first.Count(attempt.RunId));
    }

    [Fact]
    public async Task ReportPublish_replay_after_terminal_and_new_host_is_idempotent_but_changes_conflict()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        await using var first = await ReportHost.Start(database.DataSource, key: key);
        var attempt = await first.Seed("""{"reports":{"maxCount":1}}""");
        var reportId = Guid.CreateVersion7();
        using (var committed = await first.Publish(attempt, reportId, Bytes))
            Assert.Equal(HttpStatusCode.Created, committed.StatusCode);
        var capability = first.Capability(attempt);
        await using (var terminal = database.DataSource.CreateCommand("""
            WITH finished AS (UPDATE task_attempts SET status=4 WHERE run_id=$1 RETURNING run_id)
            UPDATE runs SET status=3 WHERE id=$1
            """))
        {
            terminal.Parameters.AddWithValue(attempt.RunId);
            await terminal.ExecuteNonQueryAsync(Token);
        }
        await using var replacement = await ReportHost.Start(database.DataSource, first.Root, key);
        using (var replay = await replacement.Publish(attempt, reportId, Bytes, capability: capability))
            Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        using (var changed = await replacement.Publish(attempt, reportId, Bytes, title: "Different", capability: capability))
            Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        using (var newId = await replacement.Publish(attempt, Guid.CreateVersion7(), Bytes))
            Assert.Equal(HttpStatusCode.Forbidden, newId.StatusCode);
        Assert.Equal(1, await replacement.Count(attempt.RunId));
        Assert.Equal(Bytes, await replacement.Read(attempt.RunId, reportId));
    }

    [Theory]
    [InlineData("tampered")]
    [InlineData("expired")]
    [InlineData("unknown-attempt")]
    [InlineData("other-task")]
    public async Task ReportPublish_rejects_invalid_capability_or_undeclared_attempt_without_writing(string scenario)
    {
        await using var host = await ReportHost.Start(database.DataSource);
        var attempt = await host.Seed();
        var capability = host.Capability(attempt);
        if (scenario == "tampered")
            capability = (capability[0] == 'A' ? 'B' : 'A') + capability[1..];
        if (scenario == "expired")
        {
            using var expired = new ReportUploadCapabilities(host.Key,
                new FixedTime(DateTimeOffset.UtcNow.AddDays(-2)));
            capability = expired.Create(Reference(attempt), DateTimeOffset.UtcNow.AddDays(-2)).BearerToken;
        }
        if (scenario == "unknown-attempt")
            attempt = attempt with { Attempt = 2 };
        if (scenario == "other-task")
            attempt = attempt with { TaskKey = "$post/not-declared" };
        if (scenario is "unknown-attempt" or "other-task")
            capability = host.Capability(attempt);
        using var response = await host.Publish(attempt, Guid.CreateVersion7(), Bytes, capability: capability);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await host.Count(attempt.RunId));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong-scheme")]
    [InlineData("query")]
    public async Task ReportPublish_capabilities_are_required_in_Authorization_never_in_request_URLs(string scenario)
    {
        await using var host = await ReportHost.Start(database.DataSource);
        var attempt = await host.Seed();
        using var request = host.Request(attempt, Guid.CreateVersion7(), Bytes, Metadata(Bytes));
        var capability = request.Headers.Authorization!.Parameter!;
        Assert.DoesNotContain(capability, request.RequestUri!.AbsoluteUri, StringComparison.Ordinal);
        request.Headers.Authorization = scenario == "wrong-scheme" ? new("Basic", capability) : null;
        if (scenario == "query")
            request.RequestUri = new Uri(request.RequestUri.AbsoluteUri + "?capability=" + capability);
        using var response = await host.Client.SendAsync(request, Token);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(0, await host.Count(attempt.RunId));
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("short")]
    [InlineData("long")]
    [InlineData("kind")]
    [InlineData("title")]
    [InlineData("tags")]
    [InlineData("header")]
    [InlineData("content-type")]
    [InlineData("id")]
    public async Task ReportPublish_rejects_bad_bytes_or_metadata_without_rows_or_final_objects(string scenario)
    {
        await using var host = await ReportHost.Start(database.DataSource);
        var attempt = await host.Seed();
        var id = Guid.CreateVersion7();
        var metadata = Metadata(Bytes);
        var body = Bytes;
        switch (scenario)
        {
            case "hash": metadata = metadata with { Sha256 = new string('0', 64) }; break;
            case "short": metadata = metadata with { SizeBytes = Bytes.Length + 1 }; break;
            case "long": metadata = metadata with { SizeBytes = Bytes.Length - 1 }; break;
            case "kind": metadata = metadata with { Kind = " " }; break;
            case "title": metadata = metadata with { Title = new string('x', 513) }; break;
            case "tags": metadata = metadata with { Tags = [null!] }; break;
        }
        using var request = host.Request(attempt, id, body, metadata);
        if (scenario == "header")
        {
            request.Headers.Remove(ReportUploadMetadata.HeaderName);
            request.Headers.Add(ReportUploadMetadata.HeaderName, "not-base64!!!");
        }
        if (scenario == "content-type")
            request.Content!.Headers.ContentType = new("text/plain");
        if (scenario == "id")
            request.RequestUri = new Uri(request.RequestUri!.AbsoluteUri.Replace(id.ToString("D"), Guid.NewGuid().ToString("D")));
        using var response = await host.Client.SendAsync(request, Token);
        Assert.Equal(scenario switch
        {
            "content-type" => HttpStatusCode.UnsupportedMediaType,
            "short" or "long" => HttpStatusCode.RequestEntityTooLarge,
            _ => HttpStatusCode.BadRequest
        }, response.StatusCode);
        Assert.Equal(0, await host.Count(attempt.RunId));
        Assert.False(host.Exists(attempt.RunId, id));
        Assert.Empty(Directory.GetFiles(host.Root, ".upload-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ReportPublish_storage_stream_failure_rolls_back_and_same_ID_can_repair_without_phantom_event()
    {
        await using var host = await ReportHost.Start(database.DataSource);
        var attempt = await host.Seed();
        var id = Guid.CreateVersion7();
        var store = new ReportStore(database.DataSource, host.Blobs, TimeProvider.System);
        await Assert.ThrowsAsync<IOException>(() => store.PublishAsync(attempt, id, Metadata(Bytes), new BrokenStream(), Token));
        Assert.Equal(0, await host.Count(attempt.RunId));
        Assert.False(host.Exists(attempt.RunId, id));
        using var repaired = await host.Publish(attempt, id, Bytes);
        Assert.Equal(HttpStatusCode.Created, repaired.StatusCode);
        Assert.Equal(1, await host.Count(attempt.RunId));
    }

    [Theory]
    [InlineData("""{"reports":{"maxCount":0}}""")]
    [InlineData("""{"reports":null}""")]
    public async Task ReportPublish_invalid_stored_type_policy_fails_sanitized_without_silent_defaults(string limits)
    {
        await using var host = await ReportHost.Start(database.DataSource);
        var attempt = await host.Seed(limits);
        var id = Guid.CreateVersion7();
        using var response = await host.Publish(attempt, id, Bytes);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var error = await response.Content.ReadAsStringAsync(Token);
        Assert.DoesNotContain("Exception", error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("workflow_types", error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await host.Count(attempt.RunId));
        Assert.False(host.Exists(attempt.RunId, id));
    }

    [Fact]
    public async Task ReportPublish_global_report_identity_cannot_be_reused_across_run_month_partitions()
    {
        await using var host = await ReportHost.Start(database.DataSource);
        var first = await host.Seed();
        var other = await host.Seed(at: DateTimeOffset.UtcNow.AddMonths(-1));
        var id = Guid.CreateVersion7();
        using (var accepted = await host.Publish(first, id, Bytes))
            Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        using (var collision = await host.Publish(other, id, Bytes))
            Assert.Equal(HttpStatusCode.Conflict, collision.StatusCode);
        Assert.Equal(0, await host.Count(other.RunId));
        Assert.False(host.Exists(other.RunId, id));
        Assert.Equal(Bytes, await host.Read(first.RunId, id));
    }

    [Fact]
    public async Task ReportPublish_empty_payload_is_valid_and_replay_still_verifies_actual_bytes()
    {
        await using var host = await ReportHost.Start(database.DataSource);
        var attempt = await host.Seed();
        var emptyId = Guid.CreateVersion7();
        using (var empty = await host.Publish(attempt, emptyId, []))
            Assert.Equal(HttpStatusCode.Created, empty.StatusCode);
        Assert.Empty(await host.Read(attempt.RunId, emptyId));
        var id = Guid.CreateVersion7();
        using (var original = await host.Publish(attempt, id, Bytes))
            Assert.Equal(HttpStatusCode.Created, original.StatusCode);
        using var request = host.Request(attempt, id, new byte[Bytes.Length], Metadata(Bytes));
        using var corrupt = await host.Client.SendAsync(request, Token);
        Assert.Equal(HttpStatusCode.BadRequest, corrupt.StatusCode);
        Assert.Equal(Bytes, await host.Read(attempt.RunId, id));
        Assert.Equal(2, await host.Count(attempt.RunId));
    }

    [Theory]
    [InlineData("report", 1, TaskOutcome.Succeeded)]
    [InlineData("reportBurst", 10, TaskOutcome.Failed)]
    [InlineData("reportBurst", 12, TaskOutcome.Succeeded)]
    public async Task ReportPublish_actual_gateway_agent_package_and_SDK_child_post_stage_use_authoritative_policy(
        string task, int expectedCount, TaskOutcome expectedOutcome)
    {
        await using var host = await ReportHost.Start(database.DataSource);
        var archive = await Package();
        host.App.MapGet("/package.zip", () => Microsoft.AspNetCore.Http.Results.Bytes(archive, "application/zip"));
        var hash = Convert.ToHexStringLower(SHA256.HashData(archive));
        var id = Guid.NewGuid();
        var options = new AgentOptions([host.GatewayAddress], id.ToString("D"), "report-" + id.ToString("N"),
            host.Pool, 2, heartbeatInterval: TimeSpan.FromMilliseconds(50),
            resultJournalDirectory: Path.Combine(host.Root, "journal"));
        using var cache = new PackageCache(Path.Combine(host.Root, "packages"), NullLogger<PackageCache>.Instance);
        await using var hosts = new TaskHostPool(NullLogger<TaskHostPool>.Instance, options: options);
        using var outbox = new AgentOutbox();
        await using var commands = new TaskCommands(options, cache, hosts, outbox, TimeProvider.System,
            NullLogger<TaskCommands>.Instance);
        using var connection = new AgentConnection(options, new AgentTelemetry(TimeProvider.System, commands), commands,
            TimeProvider.System, NullLogger<AgentConnection>.Instance, outbox);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var connected = connection.RunAsync(stopping.Token);
        try
        {
            await Until(() => Task.FromResult(host.Registry.Find(id) is not null), connected);
            var attempt = await host.Seed(expectedCount == 12 ? """{"reports":{"maxCount":12}}""" : null,
                dispatch: false, task: task);
            var run = await host.Runs.LoadAsync(attempt.RunId, Token);
            var command = Command(attempt, hash, new Uri(host.Address, "/package.zip"));
            using var dispatcher = new Dispatcher(database.DataSource, host.Runs, host.Registry,
                new PlacementEngine(), TimeProvider.System);
            var decision = await dispatcher.DispatchAsync(run,
                new(attempt.RunId, "$post", task, 1, hash, new(new(pool: host.Pool))), command, Token);
            Assert.Equal(id, decision.Agent!.Id);
            await Until(async () => (await host.Runs.LoadAsync(attempt.RunId, Token)).Snapshot.Tasks[attempt.TaskKey]
                is ExecutionStatus.Succeeded or ExecutionStatus.Failed, connected);
            var final = await host.Runs.LoadAsync(attempt.RunId, Token);
            await using (var error = database.DataSource.CreateCommand(
                "SELECT error FROM task_attempts WHERE run_id=$1 AND task_key=$2"))
            {
                error.Parameters.AddWithValue(attempt.RunId);
                error.Parameters.AddWithValue(attempt.TaskKey);
                var message = await error.ExecuteScalarAsync(Token);
                Assert.True(final.Snapshot.Tasks[attempt.TaskKey] ==
                    (expectedOutcome == TaskOutcome.Succeeded ? ExecutionStatus.Succeeded : ExecutionStatus.Failed),
                    $"Unexpected task outcome; recorded error: {message}");
            }
            Assert.Equal(expectedOutcome == TaskOutcome.Succeeded ? ExecutionStatus.Succeeded : ExecutionStatus.Failed,
                final.Snapshot.Tasks[attempt.TaskKey]);
            Assert.Equal(expectedCount, await host.Count(attempt.RunId));
            await using var rows = database.DataSource.CreateCommand(
                "SELECT source_key,blob_uri FROM reports WHERE run_id=$1 ORDER BY created_at,id");
            rows.Parameters.AddWithValue(attempt.RunId);
            await using var reader = await rows.ExecuteReaderAsync(Token);
            while (await reader.ReadAsync(Token))
            {
                Assert.Equal("$post/" + task, reader.GetString(0));
                Assert.StartsWith($"reports/{attempt.RunId:D}/", reader.GetString(1));
            }
            if (task == "report")
            {
                await reader.DisposeAsync();
                await using var first = database.DataSource.CreateCommand("SELECT id FROM reports WHERE run_id=$1");
                first.Parameters.AddWithValue(attempt.RunId);
                var reportId = (Guid)(await first.ExecuteScalarAsync(Token))!;
                Assert.Equal("Published by $post/report", Encoding.UTF8.GetString(await host.Read(attempt.RunId, reportId)));
            }
        }
        finally
        {
            await stopping.CancelAsync();
            await connected.WaitAsync(TimeSpan.FromSeconds(10), Token);
        }
    }

    private static async Task Until(Func<Task<bool>> condition, Task background)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (!await condition())
        {
            if (background.IsCompleted)
                await background;
            await Task.Delay(10, timeout.Token);
        }
    }

    private static ReportUploadMetadata Metadata(byte[] bytes, string title = "Summary") =>
        new("text", title, ["review"], Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length);

    private static AttemptRef Reference(ResultIdentity attempt)
    {
        var keys = attempt.TaskKey.Split('/');
        return new() { RunId = attempt.RunId.ToString("D"), NodeKey = keys[0], TaskKey = keys[1], Attempt = (uint)attempt.Attempt };
    }

    private static DispatchCommand Command(ResultIdentity attempt, string hash, Uri? package = null) => new()
    {
        Task = Reference(attempt), Version = "1.0.0", Sha256 = hash,
        PackageUri = (package ?? new Uri("https://packages.invalid/package.zip")).AbsoluteUri,
        DeadlineAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(2))
    };

    internal static async Task<byte[]> Package()
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var path in Directory.EnumerateFiles(AppContext.BaseDirectory)
                         .Where(path => Path.GetExtension(path) is ".dll" or ".json"))
            {
                var entry = archive.CreateEntry(Path.GetFileName(path));
                await using var input = File.OpenRead(path);
                await using var output = entry.Open();
                await input.CopyToAsync(output, Token);
            }
            await using var manifest = new StreamWriter(archive.CreateEntry(PackageCache.ManifestName).Open(), Encoding.UTF8);
            await manifest.WriteAsync($$"""{"version":"1.0.0","entryAssembly":"{{Path.GetFileName(typeof(FixtureMarker).Assembly.Location)}}"}""".AsMemory(), Token);
        }
        return buffer.ToArray();
    }

    internal sealed class ReportHost : IAsyncDisposable
    {
        public WebApplication App { get; private init; } = null!;
        public string Root { get; private init; } = "";
        public byte[] Key { get; private init; } = [];
        public string Pool { get; } = "reports-" + Guid.NewGuid().ToString("N");
        public Uri Address { get; private set; } = null!;
        public Uri GatewayAddress { get; private set; } = null!;
        public HttpClient Client { get; private set; } = null!;
        public FileSystemBlobStore Blobs { get; private init; } = null!;
        public ReportUploadCapabilities Capabilities { get; private init; } = null!;
        public AgentRegistry Registry { get; private init; } = null!;
        public RunStore Runs { get; private init; } = null!;
        private NpgsqlDataSource Source { get; init; } = null!;
        private bool ownsRoot;

        public static async Task<ReportHost> Start(NpgsqlDataSource source, string? root = null, byte[]? key = null,
            Action<WebApplicationBuilder>? configureServices = null, Action<WebApplication>? configure = null)
        {
            var actualKey = key ?? RandomNumberGenerator.GetBytes(32);
            var project = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (project.Parent is not null && !Directory.Exists(Path.Combine(project.FullName, ".git")))
                project = project.Parent;
            var actualRoot = root ?? Path.Combine(project.FullName, ".agent-artifacts", "tests", "reports", Guid.NewGuid().ToString("N")[..12]);
            var blobs = new FileSystemBlobStore(actualRoot, actualKey);
            var capabilities = new ReportUploadCapabilities(actualKey, TimeProvider.System);
            var registry = new AgentRegistry(source, TimeProvider.System);
            var runs = new RunStore(source, NullLogger<RunStore>.Instance);
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            var httpPort = 0;
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http1);
                options.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http2);
            });
            builder.Services.AddGrpc();
            var documentName = "v1";
            builder.Services.AddOpenApi(documentName);
            builder.Services.AddSingleton(source);
            builder.Services.AddSingleton<IBlobStore>(blobs);
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddSingleton(capabilities);
            builder.Services.AddSingleton<ReportStore>();
            builder.Services.AddSingleton(registry);
            builder.Services.AddSingleton(runs);
            configureServices?.Invoke(builder);
            var app = builder.Build();
            configure?.Invoke(app);
            app.MapReportPublishEndpoints();
            app.MapOpenApi();
            app.MapGrpcService<AgentGatewayService>();
            await app.StartAsync(Token);
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.ToArray();
            var host = new ReportHost
            {
                Source = source, Root = actualRoot, Key = actualKey, Blobs = blobs, Capabilities = capabilities,
                Registry = registry, Runs = runs, App = app, ownsRoot = root is null
            };
            host.Address = new Uri(addresses[httpPort]);
            host.GatewayAddress = new Uri(addresses[1]);
            host.Client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { BaseAddress = host.Address };
            return host;
        }

        public async Task<ResultIdentity> Seed(string? limits = null, bool dispatch = true, string task = "report",
            DateTimeOffset? at = null)
        {
            var team = "reports-" + Guid.NewGuid().ToString("N");
            await Execute("INSERT INTO teams(id,name) VALUES($1,$1)", team);
            await Execute("""
                INSERT INTO workflow_types(id,team_id,name,normalized_name,description,metadata_schema,limits)
                VALUES($1||'/reports',$1,'reports','reports','Report integration','{}'::jsonb,$2::jsonb)
                """, team, (object?)limits ?? DBNull.Value);
            var workflow = team + "/reports/workflow";
            await Execute("INSERT INTO workflows(id,type_id) VALUES($1,$2)", workflow, team + "/reports");
            var run = new RunState(Guid.CreateVersion7(at ?? DateTimeOffset.UtcNow), [new("$post", [], [new(task)])],
                new("reports", ImmutableSortedDictionary<string, string>.Empty), Assert.Fail);
            await Runs.CreateAsync(run, workflow, Guid.CreateVersion7(), DateTimeOffset.UtcNow.AddMinutes(5), Token);
            await Runs.AppendAsync(run, EventEntity.Run, null, (int)RunStatus.Running, DateTimeOffset.UtcNow, Token);
            foreach (var status in new[] { ExecutionStatus.Ready, ExecutionStatus.Dispatched, ExecutionStatus.Running })
                await Runs.AppendAsync(run, EventEntity.Node, "$post", (int)status, DateTimeOffset.UtcNow, Token);
            var attempt = new ResultIdentity(run.Snapshot.RunId, "$post/" + task, 1);
            await Runs.AppendAsync(run, EventEntity.Task, attempt.TaskKey, (int)ExecutionStatus.Ready, DateTimeOffset.UtcNow, Token);
            if (dispatch)
            {
                var id = Guid.NewGuid();
                await Registry.RegisterAsync(new Hello
                {
                    Id = id.ToString("D"), Name = "report-" + id.ToString("N"), Os = "windows", Arch = "x64",
                    Cores = 2, RamMb = 8192, MaxTasks = 2, Pool = Pool, Version = "1", ProtocolVersion = 1
                }, Token);
                using var dispatcher = new Dispatcher(Source, Runs, Registry, new PlacementEngine(), TimeProvider.System);
                var hash = new string('a', 64);
                var placed = await dispatcher.DispatchAsync(run,
                    new(attempt.RunId, "$post", task, 1, hash, new(new(pool: Pool))), Command(attempt, hash), Token);
                Assert.NotNull(placed.Agent);
            }
            return attempt;
        }

        public string Capability(ResultIdentity attempt) =>
            Capabilities.Create(Reference(attempt), DateTimeOffset.UtcNow.AddMinutes(5)).BearerToken;

        public HttpRequestMessage Request(ResultIdentity attempt, Guid id, byte[] body, ReportUploadMetadata metadata,
            string? capability = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Post,
                new Uri(Address, ReportUploadMetadata.EndpointPath + "/" + id.ToString("D")))
            {
                Content = new ByteArrayContent(body)
            };
            request.Content.Headers.ContentType = new("application/octet-stream");
            request.Headers.Authorization = new("Bearer", capability ?? Capability(attempt));
            request.Headers.Add(ReportUploadMetadata.HeaderName, WebEncoders.Base64UrlEncode(
                JsonSerializer.SerializeToUtf8Bytes(metadata, ReportJsonContext.Default.ReportUploadMetadata)));
            return request;
        }

        public async Task<HttpResponseMessage> Publish(ResultIdentity attempt, Guid id, byte[] body,
            string title = "Summary", string? capability = null)
        {
            using var request = Request(attempt, id, body, Metadata(body, title), capability);
            return await Client.SendAsync(request, Token);
        }

        public async Task<long> Count(Guid runId)
        {
            await using var command = Source.CreateCommand("SELECT count(*) FROM reports WHERE run_id=$1");
            command.Parameters.AddWithValue(runId);
            return (long)(await command.ExecuteScalarAsync(Token))!;
        }

        public bool Exists(Guid runId, Guid reportId) =>
            File.Exists(Path.Combine(Root, "reports", runId.ToString("D"), reportId.ToString("D")));

        public Task<byte[]> Read(Guid runId, Guid reportId) =>
            File.ReadAllBytesAsync(Path.Combine(Root, "reports", runId.ToString("D"), reportId.ToString("D")), Token);

        private async Task Execute(string sql, params object[] values)
        {
            await using var command = Source.CreateCommand(sql);
            foreach (var value in values)
                command.Parameters.AddWithValue(value);
            await command.ExecuteNonQueryAsync(Token);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.StopAsync(Token);
            await App.DisposeAsync();
            Blobs.Dispose();
            Capabilities.Dispose();
            Registry.Dispose();
            if (ownsRoot)
                Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class BrokenStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("Injected source interruption"));
    }
}

public sealed class ReportPublishDatabase : AgentDatabase
{
    protected override IReadOnlyList<string> Migrations => Migrator.LoadScripts().Select(script => script.Name).ToArray();
}
