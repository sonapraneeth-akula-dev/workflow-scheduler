using System.Diagnostics;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Wf.Contracts.Reports;
using Wf.ControlPlane.Api;
using Wf.ControlPlane.Live;
using LiveReportAdded = Wf.ControlPlane.Live.ReportAdded;

namespace Wf.Integration.Tests.Reports;

public sealed class ReportIndexTests(ReportPublishDatabase database, ITestOutputHelper output)
    : IClassFixture<ReportPublishDatabase>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static string Path(Guid run) => $"/api/v1/runs/{run:D}/reports";
    private static readonly string[] ItemFields =
        ["attempt", "createdAt", "id", "kind", "runId", "signOff", "sizeBytes", "sourceKey", "tags", "title"];

    [Fact]
    public async Task ReportIndex_actual_PG_HTTP_returns_exact_metadata_filters_stable_historical_sources_and_required_schema()
    {
        await using var fixture = await Start();
        var attempt = await fixture.Seed(at: DateTimeOffset.UtcNow.AddMonths(-1));
        var reports = new[]
        {
            (Id: Guid.CreateVersion7(), Kind: "html", Tags: new[] { "Review", "literal' OR true--", "Review" }, Title: "<script>\r\n../../evil\".html"),
            (Id: Guid.CreateVersion7(), Kind: "text", Tags: new[] { "review" }, Title: "other"),
            (Id: Guid.CreateVersion7(), Kind: "html", Tags: new[] { "review" }, Title: "third")
        };
        foreach (var report in reports)
        {
            using var response = await Publish(fixture, attempt, report.Id, report.Kind, report.Title, report.Tags);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }
        await fixture.Execute("""
            DELETE FROM workflows WHERE id=(SELECT workflow_id FROM runs WHERE id=$1 AND run_day=uuid_v7_run_day($1))
            """, attempt.RunId);
        using var responseIndex = await fixture.Get(Path(attempt.RunId));
        Assert.Equal(HttpStatusCode.OK, responseIndex.StatusCode);
        Assert.True(responseIndex.Headers.CacheControl!.NoStore);
        Assert.Equal("DENY", Assert.Single(responseIndex.Headers.GetValues("X-Frame-Options")));
        using var json = JsonDocument.Parse(await responseIndex.Content.ReadAsStringAsync(Token));
        Assert.Equal(["items", "runId"], json.RootElement.EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal(attempt.RunId, json.RootElement.GetProperty("runId").GetGuid());
        var items = json.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(reports.Select(report => report.Id), items.Select(item => item.GetProperty("id").GetGuid()));
        foreach (var (item, expected) in items.Zip(reports))
        {
            Assert.Equal(ItemFields, item.EnumerateObject().Select(property => property.Name).Order());
            Assert.Equal(attempt.RunId, item.GetProperty("runId").GetGuid());
            Assert.Equal("$post/report", item.GetProperty("sourceKey").GetString());
            Assert.Equal(1, item.GetProperty("attempt").GetInt32());
            Assert.Equal(expected.Kind, item.GetProperty("kind").GetString());
            Assert.Equal(expected.Title, item.GetProperty("title").GetString());
            Assert.Equal(expected.Tags, item.GetProperty("tags").EnumerateArray().Select(value => value.GetString()));
            Assert.Equal(7, item.GetProperty("sizeBytes").GetInt64());
            Assert.Equal(TimeSpan.Zero, item.GetProperty("createdAt").GetDateTimeOffset().Offset);
        }
        await fixture.Execute("UPDATE reports SET created_at='2026-01-01T00:00:00Z' WHERE run_id=$1", attempt.RunId);
        Assert.Equal(reports.Select(report => report.Id).Order(), (await Index(fixture, attempt.RunId)).Items.Select(item => item.Id));
        Assert.Equal(new[] { reports[0].Id, reports[2].Id }.Order(), (await Index(fixture, attempt.RunId, "?kind=html")).Items.Select(item => item.Id));
        Assert.Equal([reports[0].Id], (await Index(fixture, attempt.RunId, "?tag=Review")).Items.Select(item => item.Id));
        Assert.Equal([reports[2].Id], (await Index(fixture, attempt.RunId, "?kind=html&tag=review")).Items.Select(item => item.Id));
        Assert.Equal([reports[0].Id], (await Index(fixture, attempt.RunId, "?tag=" + Uri.EscapeDataString("literal' OR true--"))).Items.Select(item => item.Id));
        Assert.Empty((await Index(fixture, attempt.RunId, "?kind=HTML")).Items);
        Assert.Empty((await Index(fixture, attempt.RunId, "?tag=" + Uri.EscapeDataString("' OR 1=1; --"))).Items);
        using var schemaResponse = await fixture.Get("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, schemaResponse.StatusCode);
        using var schema = JsonDocument.Parse(await schemaResponse.Content.ReadAsStringAsync(Token));
        var schemas = schema.RootElement.GetProperty("components").GetProperty("schemas");
        foreach (var (name, fields) in new[]
        {
            ("ReportIndexResponse", new[] { "items", "runId" }), ("ReportIndexItem", ItemFields)
        })
        {
            var model = schemas.GetProperty(name);
            Assert.Equal(fields, model.GetProperty("required").EnumerateArray().Select(value => value.GetString()).Order());
            foreach (var property in model.GetProperty("properties").EnumerateObject())
                Assert.DoesNotContain("\"null\"", property.Value.GetRawText(), StringComparison.Ordinal);
        }
        Assert.True(schemas.TryGetProperty("ReportAdded", out var added));
        Assert.Equal(["reportId", "runId"], added.GetProperty("properties").EnumerateObject().Select(property => property.Name).Order());
        var operation = schema.RootElement.GetProperty("paths").GetProperty("/api/v1/runs/{id}/reports").GetProperty("get");
        Assert.Equal(["kind", "tag"], operation.GetProperty("parameters").EnumerateArray()
            .Where(parameter => parameter.GetProperty("in").GetString() == "query")
            .Select(parameter => parameter.GetProperty("name").GetString()).Order());
    }

    [Fact]
    public async Task ReportIndex_actual_HTTP_checks_fresh_Viewer_scope_identity_revocation_and_bounded_duplicate_filters()
    {
        await using var fixture = await Start();
        var attempt = await fixture.Seed();
        var credential = fixture.Credential();
        Assert.Empty((await Index(fixture, attempt.RunId)).Items);
        foreach (var query in new[]
        {
            "?kind=", "?tag=%20", "?kind=text&kind=text", "?tag=a&tag=b", "?kind=text&Kind=text", "?tags=a",
            "?kind=" + new string('a', 129), "?tag=" + Uri.EscapeDataString(new string('é', 65)), "?tag=%00"
        })
        {
            using var invalid = await fixture.Get(Path(attempt.RunId) + query);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        using (var boundary = await fixture.Get(Path(attempt.RunId) + "?tag=" + Uri.EscapeDataString(new string('é', 64))))
            Assert.Equal(HttpStatusCode.OK, boundary.StatusCode);
        foreach (var denied in new[]
        {
            fixture.Credential(user: Guid.NewGuid()), fixture.Credential(identity: "app"),
            fixture.Credential(role: "Agent"), fixture.Credential(role: "Registrar"),
            fixture.Credential(extra: [new Claim("_claim_names", "{\"groups\":\"source\"}")])
        })
        {
            using var response = await fixture.Get(Path(attempt.RunId), denied);
            await PrivateFailure(response, HttpStatusCode.Forbidden);
        }
        using (var unknown = await fixture.Get(Path(Guid.CreateVersion7())))
            await PrivateFailure(unknown, HttpStatusCode.Forbidden);
        using (var anonymous = await fixture.Get(Path(attempt.RunId), anonymous: true))
            await PrivateFailure(anonymous, HttpStatusCode.Unauthorized);
        using (var invalid = await fixture.Get("/api/v1/runs/not-a-run/reports"))
            await PrivateFailure(invalid, HttpStatusCode.BadRequest);
        await fixture.Execute("DELETE FROM role_bindings WHERE principal_id=$1", fixture.User);
        using var revoked = await fixture.Get(Path(attempt.RunId), credential);
        await PrivateFailure(revoked, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ReportIndex_actual_HTTP_POST_commit_LISTEN_MessagePack_only_authorized_run_receives_ReportAdded_within_one_second()
    {
        await using var fixture = await Start(live: true);
        var attempt = await fixture.Seed();
        var other = await fixture.Seed();
        await using var viewer = await Client.Connect(fixture);
        await using var differentRun = await Client.Connect(fixture);
        await using var denied = await Client.Connect(fixture, fixture.Credential(user: Guid.NewGuid()));
        Assert.Null(await viewer.Subscribe(attempt.RunId, uppercase: true));
        Assert.Null(await differentRun.Subscribe(other.RunId));
        Assert.Equal(403, (await denied.Subscribe(attempt.RunId))!.Status);
        var sequence = await Sequence(fixture, attempt.RunId);
        var timings = new List<double>();
        for (var index = 0; index < 6; index++)
        {
            var id = Guid.CreateVersion7();
            var started = Stopwatch.GetTimestamp();
            using (var published = await Publish(fixture, attempt, id))
                Assert.Equal(HttpStatusCode.Created, published.StatusCode);
            var report = await viewer.Read();
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            timings.Add(elapsed);
            Assert.Equal(attempt.RunId.ToString("D"), report.RunId);
            Assert.Equal(id.ToString("D"), report.ReportId);
            Assert.True(elapsed <= 1000, $"ReportAdded publish-to-delivery took {elapsed:F3} ms.");
            using var duplicate = await Publish(fixture, attempt, id);
            Assert.Equal(HttpStatusCode.Created, duplicate.StatusCode);
        }
        await Task.Delay(200, Token);
        Assert.False(viewer.Reports.Reader.TryPeek(out _));
        Assert.False(viewer.Runs.Reader.TryPeek(out _));
        Assert.False(differentRun.Reports.Reader.TryPeek(out _));
        Assert.False(denied.Reports.Reader.TryPeek(out _));
        Assert.Equal(sequence, await Sequence(fixture, attempt.RunId));
        Assert.Equal(6, (await Index(fixture, attempt.RunId)).Items.Length);
        output.WriteLine($"ReportIndex actual POST -> COMMIT -> LISTEN -> authenticated Viewer MessagePack timings ms: {string.Join(", ", timings.Select(value => value.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)))}; max={timings.Max():F3} ms.");
    }

    [Fact]
    public async Task ReportIndex_actual_rollback_failed_publish_and_missing_durable_row_emit_no_ReportAdded_and_revocation_is_fresh()
    {
        await using var fixture = await Start(live: true);
        var attempt = await fixture.Seed();
        await using var viewer = await Client.Connect(fixture);
        Assert.Null(await viewer.Subscribe(attempt.RunId));
        var original = Guid.CreateVersion7();
        using (var published = await Publish(fixture, attempt, original))
            Assert.Equal(HttpStatusCode.Created, published.StatusCode);
        Assert.Equal(original.ToString("D"), (await viewer.Read()).ReportId);
        await using (var connection = await database.DataSource.OpenConnectionAsync(Token))
        await using (var transaction = await connection.BeginTransactionAsync(Token))
        {
            var rolledBack = Guid.CreateVersion7();
            await using var query = new NpgsqlCommand("""
                WITH locator AS (INSERT INTO report_ids(id,run_day) VALUES($1,uuid_v7_run_day($2)) RETURNING id)
                INSERT INTO reports(run_day,id,run_id,source_key,attempt,kind,title,tags,size_bytes,blob_uri,sha256,created_at)
                SELECT run_day,$1,run_id,source_key,attempt,kind,title,tags,size_bytes,
                    'reports/'||run_id::text||'/'||$1::uuid::text,sha256,created_at
                FROM reports WHERE run_day=uuid_v7_run_day($2) AND run_id=$2 AND id=$3
                """, connection, transaction);
            query.Parameters.AddWithValue(rolledBack);
            query.Parameters.AddWithValue(attempt.RunId);
            query.Parameters.AddWithValue(original);
            await query.ExecuteNonQueryAsync(Token);
            await Task.Delay(200, Token);
            Assert.False(viewer.Reports.Reader.TryPeek(out _));
            await transaction.RollbackAsync(Token);
        }
        var bad = Encoding.UTF8.GetBytes("payload");
        using (var request = fixture.Host.Request(attempt, Guid.CreateVersion7(), bad,
            new("text", "Summary", [], new string('0', 64), bad.Length)))
        using (var response = await fixture.Host.Client.SendAsync(request, Token))
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var absent = Guid.CreateVersion7();
        await fixture.Execute("SELECT pg_notify('report_added',$1)", JsonSerializer.Serialize(new
            { kind = "ReportAdded", runId = attempt.RunId, reportId = absent }));
        await Task.Delay(200, Token);
        Assert.False(viewer.Reports.Reader.TryPeek(out _));
        Assert.Single((await Index(fixture, attempt.RunId)).Items);
        await fixture.Execute("SELECT pg_notify('report_added',$1)", "not-json");
        Assert.Equal(attempt.RunId.ToString("D"), (await viewer.ReadResync()).Id);
        await fixture.Execute("DELETE FROM role_bindings WHERE principal_id=$1", fixture.User);
        var afterRevocation = Guid.CreateVersion7();
        using (var response = await Publish(fixture, attempt, afterRevocation))
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var resync = await viewer.ReadResync();
        Assert.Equal("run", resync.Scope);
        Assert.Equal(attempt.RunId.ToString("D"), resync.Id);
        Assert.False(viewer.Reports.Reader.TryPeek(out _));
        Assert.Equal(403, (await viewer.Subscribe(attempt.RunId))!.Status);
        using var deniedIndex = await fixture.Get(Path(attempt.RunId));
        await PrivateFailure(deniedIndex, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ReportIndex_listener_reconnect_explicitly_resyncs_durable_index_and_transport_reconnect_refetches_without_REST_polling()
    {
        await using var fixture = await Start(live: true);
        var attempt = await fixture.Seed();
        await using var viewer = await Client.Connect(fixture);
        Assert.Null(await viewer.Subscribe(attempt.RunId));
        while (viewer.Resyncs.Reader.TryRead(out _)) { }
        await fixture.Execute("""
            SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname=current_database()
                AND pid<>pg_backend_pid() AND application_name='wf-live-feed'
            """);
        using (var down = CancellationTokenSource.CreateLinkedTokenSource(Token))
        {
            down.CancelAfter(TimeSpan.FromSeconds(10));
            while (fixture.Host.App.Services.GetRequiredService<LiveFanout>().IsReady)
                await Task.Delay(10, down.Token);
        }
        var id = Guid.CreateVersion7();
        using (var response = await Publish(fixture, attempt, id))
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var reset = await viewer.ReadResync();
        Assert.Equal("run", reset.Scope);
        Assert.Equal(attempt.RunId.ToString("D"), reset.Id);
        Assert.Equal(id, Assert.Single((await Index(fixture, attempt.RunId)).Items).Id);
        while (viewer.Reports.Reader.TryRead(out _)) { }
        var next = Guid.CreateVersion7();
        using (var response = await Publish(fixture, attempt, next))
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(next.ToString("D"), (await viewer.Read()).ReportId);
        await viewer.Connection.StopAsync(Token);
        var offline = Guid.CreateVersion7();
        using (var response = await Publish(fixture, attempt, offline))
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await viewer.Connection.StartAsync(Token);
        Assert.Null(await viewer.Subscribe(attempt.RunId));
        // This is a reconnect resync, not a REST poll or synthesized lifecycle sequence.
        Assert.Equal([id, next, offline], (await Index(fixture, attempt.RunId)).Items.Select(item => item.Id));
        Assert.False(viewer.Runs.Reader.TryPeek(out _));
    }

    [Fact]
    public async Task ReportIndex_actual_authenticated_index_p95_is_under_300ms()
    {
        await using var fixture = await Start();
        var attempt = await fixture.Seed();
        for (var index = 0; index < 10; index++)
        {
            using var response = await Publish(fixture, attempt, Guid.CreateVersion7());
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }
        await Index(fixture, attempt.RunId);
        var times = new List<double>();
        for (var index = 0; index < 100; index++)
        {
            var started = Stopwatch.GetTimestamp();
            var result = await Index(fixture, attempt.RunId);
            Assert.Equal(10, result.Items.Length);
            times.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        times.Sort();
        var p95 = times[94];
        output.WriteLine($"ReportIndex actual PG17/Kestrel/RSA authenticated HTTP+JSON, 10 durable reports, n=100: p50={times[49]:F3} ms, p95={p95:F3} ms, max={times[^1]:F3} ms.");
        Assert.True(p95 < 300, $"Report index p95 {p95:F3} ms is not below 300 ms.");
    }

    [Fact]
    public async Task ReportIndex_actual_database_unavailable_returns_private_503()
    {
        await using var unavailable = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Username=report_fixture;Database=unavailable;Timeout=1");
        await using var fixture = await Start(source: unavailable);
        using var response = await fixture.Get(Path(Guid.CreateVersion7()));
        await PrivateFailure(response, HttpStatusCode.ServiceUnavailable);
    }

    private async Task<ReportReadFixture> Start(bool live = false, NpgsqlDataSource? source = null)
    {
        var fixture = await ReportReadFixture.Start(source ?? database.DataSource, services: builder =>
        {
            builder.Services.ConfigureHttpJsonOptions(options =>
                options.SerializerOptions.TypeInfoResolverChain.Insert(0, ReportIndexJsonContext.Default));
            builder.Services.AddWorkflowOpenApi();
            if (live) builder.Services.AddWorkflowLive();
        }, configure: app =>
        {
            app.MapReportIndexEndpoints();
            if (live) app.MapHub<LiveHub>(LiveHub.Path, options => options.CloseOnAuthenticationExpiration = true);
        });
        if (live)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            while (!fixture.Host.App.Services.GetRequiredService<LiveFanout>().IsReady)
                await Task.Delay(10, timeout.Token);
        }
        return fixture;
    }

    private static async Task<HttpResponseMessage> Publish(ReportReadFixture fixture,
        Wf.Engine.Core.Results.ResultIdentity attempt, Guid id, string kind = "text", string title = "Summary", string[]? tags = null)
    {
        var bytes = Encoding.UTF8.GetBytes("payload");
        using var request = fixture.Host.Request(attempt, id, bytes, new ReportUploadMetadata(
            kind, title, tags ?? ["review"], Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length));
        return await fixture.Host.Client.SendAsync(request, Token);
    }

    private static async Task<ReportIndexResponse> Index(ReportReadFixture fixture, Guid run, string query = "")
    {
        using var response = await fixture.Get(Path(run) + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(Token),
            ReportIndexJsonContext.Default.ReportIndexResponse)!;
    }

    private static async Task<long> Sequence(ReportReadFixture fixture, Guid run)
    {
        await using var query = fixture.Source.CreateCommand("SELECT last_seq FROM runs WHERE run_day=uuid_v7_run_day($1) AND id=$1");
        query.Parameters.AddWithValue(run);
        return (long)(await query.ExecuteScalarAsync(Token))!;
    }

    private static async Task PrivateFailure(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
        var text = await response.Content.ReadAsStringAsync(Token);
        foreach (var secret in new[] { "blob_uri", "sha256", "Bearer", "variables", "Npgsql", "Exception", "reports/" })
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
    }

    private sealed class Client(HubConnection connection) : IAsyncDisposable
    {
        public HubConnection Connection => connection;
        public Channel<LiveReportAdded> Reports { get; } = Channel.CreateUnbounded<LiveReportAdded>();
        public Channel<RunDelta> Runs { get; } = Channel.CreateUnbounded<RunDelta>();
        public Channel<LiveResync> Resyncs { get; } = Channel.CreateUnbounded<LiveResync>();

        public static async Task<Client> Connect(ReportReadFixture fixture, string? credential = null)
        {
            var connection = new HubConnectionBuilder().WithUrl(new Uri(fixture.Host.Address, LiveHub.Path), options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(credential ?? fixture.Credential());
                options.Transports = HttpTransportType.WebSockets;
                options.SkipNegotiation = true;
            }).AddMessagePackProtocol().Build();
            var client = new Client(connection);
            connection.On<LiveReportAdded>("ReportAdded", message => client.Reports.Writer.TryWrite(message));
            connection.On<RunDelta>("RunDelta", message => client.Runs.Writer.TryWrite(message));
            connection.On<LiveResync>("Resync", message => client.Resyncs.Writer.TryWrite(message));
            await connection.StartAsync(Token);
            return client;
        }

        public Task<LiveSubscriptionProblem?> Subscribe(Guid run, bool uppercase = false) => connection.InvokeAsync<LiveSubscriptionProblem?>(
            "Subscribe", "run", uppercase ? run.ToString("D").ToUpperInvariant() : run.ToString("D"), Token);

        public async Task<LiveReportAdded> Read()
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            return await Reports.Reader.ReadAsync(timeout.Token);
        }

        public async Task<LiveResync> ReadResync()
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            return await Resyncs.Reader.ReadAsync(timeout.Token);
        }

        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }
}
