using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wf.ControlPlane.Api;

namespace Wf.ControlPlane.Tests;

public sealed class ReportContentHeadersTests
{
    [Theory]
    [InlineData("html", "text/html; charset=utf-8", "html")]
    [InlineData("svg", "image/svg+xml", "svg")]
    [InlineData("image/svg+xml", "image/svg+xml", "svg")]
    [InlineData("png", "image/png", "png")]
    [InlineData("image/jpeg", "image/jpeg", "jpg")]
    [InlineData("pdf", "application/pdf", "pdf")]
    [InlineData("json", "application/json; charset=utf-8", "json")]
    [InlineData("chart", "application/json; charset=utf-8", "json")]
    [InlineData("markdown", "text/plain; charset=utf-8", "txt")]
    [InlineData("table", "text/plain; charset=utf-8", "txt")]
    [InlineData("diff", "text/plain; charset=utf-8", "txt")]
    public void ReportContentHeaders_only_allowlisted_kinds_are_inline(string kind, string mime, string extension)
    {
        Assert.Equal((mime, extension, true), ReportContentEndpoints.ContentType(kind));
        Assert.Equal("sandbox allow-scripts; frame-ancestors 'self'; default-src 'none'; " +
            "script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src data: blob:; font-src data:; " +
            "connect-src 'none'; form-action 'none'; base-uri 'none'", ReportContentEndpoints.ContentPolicy);
        Assert.DoesNotContain("allow-same-origin", ReportContentEndpoints.ContentPolicy);
        Assert.DoesNotContain("allow-top-navigation", ReportContentEndpoints.ContentPolicy);
    }

    [Theory]
    [InlineData("evil")]
    [InlineData("text/html\r\nX-Injected: true")]
    [InlineData("../../html")]
    [InlineData("HTML")]
    [InlineData("image")]
    [InlineData("")]
    public void ReportContentHeaders_unknown_and_injected_kinds_download_as_binary(string kind) =>
        Assert.Equal(("application/octet-stream", "bin", false), ReportContentEndpoints.ContentType(kind));

    [Fact]
    public async Task ReportContentHeaders_all_other_responses_and_lookalike_paths_keep_DENY_and_nosniff()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.UseReportResponseHeaders();
        app.MapGet("/api/v1/reports/{id}/content-evil", () => Results.Content("<script>evil()</script>", "text/html"));
        app.MapGet("/status/{code:int}", (int code) => Results.StatusCode(code));
        await app.StartAsync(TestContext.Current.CancellationToken);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient { BaseAddress = new(address) };
        foreach (var path in new[] { "/status/200", "/status/401", "/status/403", "/status/500",
            "/api/v1/reports/id/content-evil", "/missing", "/api/v1/reports/id/content" })
        {
            using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
            Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
            if (path == "/missing") Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }
}
