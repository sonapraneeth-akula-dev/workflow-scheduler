using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Wf.Sdk;
using Wf.Sdk.Hosting;

namespace Wf.Integration.Tests.LocalRun;

public sealed class RunModeSubmissionTests
{
    [Fact]
    public async Task Engine_submission_uses_real_HTTP_JSON_contract_and_prints_run_id()
    {
        var token = TestContext.Current.CancellationToken;
        var runId = Guid.CreateVersion7();
        var received = false;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build();
        app.MapPost("/api/v1/runs", async (HttpRequest request) =>
        {
            using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: token);
            Assert.Equal("tests/mode-submit", document.RootElement.GetProperty("workflowId").GetString());
            Assert.Equal("1.2.3", document.RootElement.GetProperty("version").GetString());
            Assert.Equal("ppe", document.RootElement.GetProperty("variables").GetProperty("env").GetString());
            Assert.True(Guid.TryParse(request.Headers["Idempotency-Key"], out _));
            received = true;
            return Results.Json(new { runId }, statusCode: StatusCodes.Status202Accepted);
        });
        await app.StartAsync(token);
        var workflow = Workflow.Define("tests/mode-submit", "1.2.3", w => w.Type("test")
            .Variables(v => v.String("env", required: true))
            .Node("work", n => n.Task("never", _ => throw new InvalidOperationException("must not execute"))));
        using var output = new StringWriter();
        Assert.Equal(0, await RunModeDispatcher.RunAsync(workflow, ["--engine", Address(app), "--var", "env=ppe"],
            output, cancellationToken: token));
        Assert.True(received);
        Assert.Equal(runId + Environment.NewLine, output.ToString());
    }

    [Fact]
    public async Task Default_HTTP_client_never_follows_a_run_submission_redirect()
    {
        var token = TestContext.Current.CancellationToken;
        var redirected = 0;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build();
        app.MapPost("/api/v1/runs", () => Results.Redirect("/unexpected", preserveMethod: true));
        app.MapPost("/unexpected", () =>
        {
            Interlocked.Increment(ref redirected);
            return Results.Json(new { runId = Guid.CreateVersion7() }, statusCode: StatusCodes.Status202Accepted);
        });
        await app.StartAsync(token);
        var workflow = Workflow.Define("tests/mode-submit", "1.2.3", w => w.Type("test")
            .Node("work", n => n.Task("never", _ => throw new InvalidOperationException("must not execute"))));
        using var output = new StringWriter();
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => RunModeDispatcher.RunAsync(workflow,
            ["--engine", Address(app)], output, cancellationToken: token));
        Assert.Equal(HttpStatusCode.TemporaryRedirect, error.StatusCode);
        Assert.Equal(0, Volatile.Read(ref redirected));
        Assert.Equal("", output.ToString());
    }

    private static string Address(WebApplication app) => app.Services.GetRequiredService<IServer>().Features
        .Get<IServerAddressesFeature>()!.Addresses.Single();
}
