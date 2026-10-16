using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using Wf.Contracts.Reports;
using Wf.ControlPlane.Api;
using Wf.ControlPlane.Storage;
using Wf.Integration.Tests.Storage;

namespace Wf.Integration.Tests.Reports;

public sealed class ReportContentTests(ReportPublishDatabase database, BlobStoreEnvironment environment)
    : IClassFixture<ReportPublishDatabase>, IClassFixture<BlobStoreEnvironment>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static string Path(Guid id) => $"/api/v1/reports/{id:D}/content";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReportContent_actual_PG_Kestrel_filesystem_or_Azure_streams_raw_bytes_with_direct_open_sandbox(bool azure)
    {
        await using var storage = await environment.CreateAsync(azure);
        await using var fixture = await ReportReadFixture.Start(database.DataSource, storage.Store);
        var attempt = await fixture.Seed();
        var payload = Encoding.UTF8.GetBytes("<script>top.location='https://evil.invalid';fetch('/api/v1/runs')</script>");
        foreach (var kind in new[] { "html", "svg", "text", "unknown\r\nX-Injected: yes" })
        {
            var id = Guid.CreateVersion7();
            var metadata = new ReportUploadMetadata(kind, "../../evil\"\r\nX-Injected: yes.html", ["<script>"], Hash(payload), payload.Length);
            using var request = fixture.Host.Request(attempt, id, payload, metadata);
            using var published = await fixture.Host.Client.SendAsync(request, Token);
            Assert.Equal(HttpStatusCode.Created, published.StatusCode);
            using var response = await fixture.Get(Path(id), completion: HttpCompletionOption.ResponseHeadersRead);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Null(response.Content.Headers.ContentLength);
            Assert.True(response.Headers.TransferEncodingChunked);
            Assert.Equal(payload, await response.Content.ReadAsByteArrayAsync(Token));
            Assert.Equal(ReportContentEndpoints.ContentPolicy, Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
            Assert.False(response.Headers.Contains("X-Frame-Options"));
            Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
            Assert.True(response.Headers.CacheControl!.NoStore);
            Assert.Equal(ReportContentEndpoints.ContentType(kind).Mime, response.Content.Headers.ContentType!.ToString());
            Assert.StartsWith(kind.StartsWith("unknown", StringComparison.Ordinal) ? "attachment;" : "inline;",
                response.Content.Headers.ContentDisposition!.ToString(), StringComparison.Ordinal);
            Assert.Contains(id.ToString("D"), response.Content.Headers.ContentDisposition!.FileName);
            Assert.DoesNotContain("evil", response.Content.Headers.ContentDisposition.ToString(), StringComparison.Ordinal);
            Assert.False(response.Headers.Contains("X-Injected"));
        }
        using var health = await fixture.Get("/healthz", anonymous: true);
        Assert.Equal("DENY", Assert.Single(health.Headers.GetValues("X-Frame-Options")));
        using var schema = await fixture.Get("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, schema.StatusCode);
        using var json = JsonDocument.Parse(await schema.Content.ReadAsStringAsync(Token));
        var binary = json.RootElement.GetProperty("paths").GetProperty("/api/v1/reports/{id}/content")
            .GetProperty("get").GetProperty("responses").GetProperty("200").GetProperty("content")
            .GetProperty("application/octet-stream").GetProperty("schema");
        Assert.Equal("string", binary.GetProperty("type").GetString());
        Assert.Equal("binary", binary.GetProperty("format").GetString());
    }

    [Fact]
    public async Task ReportContent_fresh_Viewer_scope_identity_revocation_and_deleted_historical_workflow_fail_closed()
    {
        await using var fixture = await ReportReadFixture.Start(database.DataSource);
        var attempt = await fixture.Seed(at: DateTimeOffset.UtcNow.AddMonths(-1));
        var id = Guid.CreateVersion7();
        using (var published = await fixture.Host.Publish(attempt, id, [1, 2, 3]))
            Assert.Equal(HttpStatusCode.Created, published.StatusCode);
        await fixture.Execute("""
            DELETE FROM workflows WHERE id=(SELECT workflow_id FROM runs WHERE id=$1 AND run_day=uuid_v7_run_day($1))
            """, attempt.RunId);
        var credential = fixture.Credential();
        using (var historical = await fixture.Get(Path(id), credential))
            Assert.Equal(HttpStatusCode.OK, historical.StatusCode);
        foreach (var denied in new[]
        {
            fixture.Credential(user: Guid.NewGuid()), fixture.Credential(identity: "app"),
            fixture.Credential(role: "Agent"), fixture.Credential(role: "Registrar"),
            fixture.Credential(extra: [new Claim("hasgroups", "true")])
        })
        {
            using var response = await fixture.Get(Path(id), denied);
            await Denial(response, HttpStatusCode.Forbidden);
        }
        foreach (var denied in new[] { fixture.Credential(expired: true), fixture.Credential(unsigned: true) })
        {
            using var response = await fixture.Get(Path(id), denied);
            await Denial(response, HttpStatusCode.Unauthorized);
        }
        using (var anonymous = await fixture.Get(Path(id), anonymous: true))
            await Denial(anonymous, HttpStatusCode.Unauthorized);
        using (var missing = await fixture.Get(Path(Guid.CreateVersion7())))
            await Denial(missing, HttpStatusCode.Forbidden);
        using (var invalid = await fixture.Get("/api/v1/reports/not-an-id/content"))
            await Denial(invalid, HttpStatusCode.BadRequest);
        using (var query = await fixture.Get(Path(id) + "?download=true"))
            await Denial(query, HttpStatusCode.BadRequest);
        await fixture.Execute("DELETE FROM role_bindings WHERE principal_id=$1", fixture.User);
        using var revoked = await fixture.Get(Path(id), credential);
        await Denial(revoked, HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReportContent_corrupt_large_blob_aborts_instead_of_success_shaped_partial_bytes_and_missing_fails_closed(bool azure)
    {
        await using var storage = await environment.CreateAsync(azure);
        await using var fixture = await ReportReadFixture.Start(database.DataSource, storage.Store);
        var attempt = await fixture.Seed();
        var id = Guid.CreateVersion7();
        var bytes = new byte[10_000_000];
        RandomNumberGenerator.Fill(bytes);
        using (var published = await fixture.Host.Publish(attempt, id, bytes))
            Assert.Equal(HttpStatusCode.Created, published.StatusCode);
        using (var valid = await fixture.Get(Path(id), completion: HttpCompletionOption.ResponseHeadersRead))
        {
            Assert.Null(valid.Content.Headers.ContentLength);
            await using var input = await valid.Content.ReadAsStreamAsync(Token);
            Assert.Equal(SHA256.HashData(bytes), await SHA256.HashDataAsync(input, Token));
        }
        var content = new BlobContent(BlobKind.Reports, Hash(bytes), bytes.Length, $"reports/{attempt.RunId:D}/{id:D}");
        bytes[^1] ^= 1;
        await storage.CorruptAsync(content, bytes);
        await Assert.ThrowsAnyAsync<HttpRequestException>(async () =>
        {
            using var corrupt = await fixture.Get(Path(id));
        });
        await storage.CorruptAsync(content, []);
        using (var truncated = await fixture.Get(Path(id)))
            await Denial(truncated, HttpStatusCode.ServiceUnavailable);
        await using var missingStorage = await environment.CreateAsync(azure);
        await using var missingFixture = await ReportReadFixture.Start(database.DataSource, missingStorage.Store);
        using var missing = await missingFixture.Get(Path(id), missingFixture.Credential(user: fixture.User));
        await Denial(missing, HttpStatusCode.ServiceUnavailable);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    [Fact]
    public async Task ReportContent_actual_database_unavailability_returns_private_fail_closed_503()
    {
        await using var unavailable = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Username=report_fixture;Database=unavailable;Timeout=1");
        await using var fixture = await ReportReadFixture.Start(unavailable);
        using var response = await fixture.Get(Path(Guid.CreateVersion7()));
        await Denial(response, HttpStatusCode.ServiceUnavailable);
    }

    private static async Task Denial(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("sandbox; frame-ancestors 'none'", Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
        Assert.True(response.Headers.CacheControl!.NoStore);
        var body = await response.Content.ReadAsStringAsync(Token);
        foreach (var secret in new[] { "blob_uri", "sha256", "reports/", "Bearer", "variables", "Npgsql", "Exception", ".agent-artifacts" })
            Assert.DoesNotContain(secret, body, StringComparison.Ordinal);
    }
}
