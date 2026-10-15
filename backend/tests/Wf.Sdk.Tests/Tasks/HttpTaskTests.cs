using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Wf.Sdk;

namespace Wf.Sdk.Tests;

public sealed class HttpTaskTests : IAsyncLifetime
{
    private WebApplication app = null!;
    private string url = "";
    private int requestCount;
    private readonly TaskCompletionSource requestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        app = builder.Build();
        app.Use((context, next) => { Interlocked.Increment(ref requestCount); return next(context); });
        app.MapPost("/echo", async context =>
        {
            context.Response.Headers["Echo"] = context.Request.Headers["Input"];
            using var reader = new StreamReader(context.Request.Body);
            await context.Response.WriteAsync(await reader.ReadToEndAsync(context.RequestAborted), context.RequestAborted);
        });
        app.MapGet("/accepted", context =>
        {
            context.Response.StatusCode = 202;
            context.Response.Headers.Location = "/must-not-poll";
            return context.Response.WriteAsync("accepted", context.RequestAborted);
        });
        app.MapGet("/fail", () => Results.StatusCode(503));
        app.MapGet("/missing", () => Results.NotFound());
        app.MapGet("/large", context => context.Response.WriteAsync(new string('x', 70_000), context.RequestAborted));
        app.MapGet("/wait", async context =>
        {
            requestStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
        });
        await app.StartAsync();
        url = app.Urls.Single();
    }

    public async ValueTask DisposeAsync() => await app.DisposeAsync();

    [Fact]
    public async Task SendsMethodHeadersAndBodyAndPublishesResponseOutput()
    {
        var headers = new Dictionary<string, string> { ["Input"] = "original" };
        var task = Tasks.Http(HttpMethod.Post, url + "/echo", headers, "{\"hello\":\"world\"}");
        headers["Input"] = "changed";
        var context = new TaskContext(TestContext.Current.CancellationToken);
        await task(context);
        var response = context.Outputs.Get<HttpResponseOutput>("response")!;
        Assert.Equal(200, response.StatusCode);
        Assert.Equal("{\"hello\":\"world\"}", response.Body);
        Assert.Equal(["original"], response.Headers["Echo"]);
    }

    [Fact]
    public async Task AcceptedIsAnOrdinaryResponseAndDoesNotPollLocation()
    {
        var context = new TaskContext(TestContext.Current.CancellationToken);
        await Tasks.Http(HttpMethod.Get, url + "/accepted")(context);
        var response = context.Outputs.Get<HttpResponseOutput>("response")!;
        Assert.Equal(202, response.StatusCode);
        Assert.Equal("accepted", response.Body);
        Assert.Equal(1, requestCount);
    }

    [Fact]
    public async Task UnexpectedStatusFailsButExplicitExpectedErrorStatusCanSucceed()
    {
        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            Tasks.Http(HttpMethod.Get, url + "/fail")(new(TestContext.Current.CancellationToken)));
        Assert.Contains("503", error.Message);
        var context = new TaskContext(TestContext.Current.CancellationToken);
        await Tasks.Http(HttpMethod.Get, url + "/missing", expectedStatus: 404)(context);
        Assert.Equal(404, context.Outputs.Get<HttpResponseOutput>("response")!.StatusCode);
    }

    [Fact]
    public async Task CancellationStopsAnInFlightRequest()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var task = Tasks.Http(HttpMethod.Get, url + "/wait")(new(cancellation.Token));
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task ResponseBufferIsBoundedBeforePublishingOutput()
    {
        var context = new TaskContext(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<HttpRequestException>(() => Tasks.Http(HttpMethod.Get, url + "/large")(context));
        Assert.Empty(context.Outputs.Values);
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("file:///tmp/file")]
    [InlineData("ftp://example.com/file")]
    public void InvalidUrlsFailAtDefinitionTime(string value) =>
        Assert.Throws<ArgumentException>(() => Tasks.Http(HttpMethod.Get, value));

    [Fact]
    public void InvalidHeadersAndStatusFailAtDefinitionTime()
    {
        Assert.Throws<FormatException>(() => Tasks.Http(HttpMethod.Get, url, new Dictionary<string, string>
            { ["Input"] = "bad\r\nheader" }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Tasks.Http(HttpMethod.Get, url, expectedStatus: 99));
    }

    [Fact]
    public async Task WorkflowDescribesHttpTaskWithoutSendingAndResolvesItsStableKey()
    {
        var workflow = Workflow.Define("http", "1", w => w.Type("test")
            .Node("call", n => n.Task("request", Tasks.Http(HttpMethod.Get, url + "/accepted"))));
        var manifest = ManifestWriter.Create(workflow);
        Assert.Equal(0, requestCount);
        Assert.Equal("request", Assert.Single(manifest.Nodes[0].Tasks).Key);
        var context = new TaskContext(TestContext.Current.CancellationToken, outputs: new("call"));
        await workflow.Tasks["call/request"](context);
        Assert.Equal(1, requestCount);
        Assert.Equal(202, context.Outputs.Get<HttpResponseOutput>("response")!.StatusCode);
    }
}
