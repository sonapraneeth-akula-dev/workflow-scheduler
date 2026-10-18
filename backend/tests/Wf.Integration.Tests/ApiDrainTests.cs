using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Wf.ControlPlane.Live;
using Wf.ControlPlane.Data;
using Wf.Integration.Tests.Auth;

namespace Wf.Integration.Tests;

public sealed class ApiDrainTests(AuthorizationDatabase database, ITestOutputHelper output) :
    IClassFixture<AuthorizationDatabase>
{
    [Fact]
    public async Task Stop_signal_withdraws_readiness_finishes_accepted_HTTP_and_reconnects_the_real_live_transport()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        var token = deadline.Token;
        await using var fixture = await LiveResyncTests.Fixture.Start(database.DataSource, enableDrain: true);
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false })
        {
            BaseAddress = new Uri(fixture.Api), Timeout = TimeSpan.FromSeconds(20)
        };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Jwt);
        await using var hub = new HubConnectionBuilder().WithUrl(new Uri(client.BaseAddress, LiveHub.Path), options =>
        {
            options.AccessTokenProvider = () => Task.FromResult<string?>(fixture.Jwt);
            options.Transports = HttpTransportType.WebSockets;
            options.SkipNegotiation = true;
        }).AddMessagePackProtocol().WithAutomaticReconnect().Build();
        var hint = new TaskCompletionSource<LiveReconnect>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On<LiveReconnect, bool>("Reconnect", message => { hint.TrySetResult(message); return true; });
        hub.Reconnected += _ => { reconnected.TrySetResult(); return Task.CompletedTask; };
        await hub.StartAsync(token);
        Assert.Null(await hub.InvokeAsync<LiveSubscriptionProblem?>("Subscribe", "run", fixture.RunId.ToString("D"), token));
        using (var ready = await client.GetAsync("/healthz/ready", token))
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        var held = client.GetAsync("/fixture/held", token);
        await fixture.RequestEntered.WaitAsync(token);
        var draining = fixture.StopApi(graceful: true);
        try
        {
            using (var ready = await client.GetAsync("/healthz/ready", token))
                Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
            for (var index = 0; index < 10; index++)
            {
                using var accepted = await client.GetAsync($"/api/v1/runs/{fixture.RunId}/events?afterSeq=0", token);
                Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
            }
            Assert.Equal("server_draining", (await hint.Task.WaitAsync(token)).Reason);
            Assert.False(held.IsCompleted);
            Assert.False(draining.IsCompleted);
        }
        finally { fixture.ReleaseRequest(); }
        using (var response = await held)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            Assert.True(body.RootElement.GetProperty("finished").GetBoolean());
        }
        await draining;
        await fixture.Append(12, 0, 0, token);
        await fixture.StartApi();
        await reconnected.Task.WaitAsync(token);
        Assert.Null(await hub.InvokeAsync<LiveSubscriptionProblem?>("Subscribe", "run", fixture.RunId.ToString("D"), token));
        using var recovered = await client.GetAsync($"/api/v1/runs/{fixture.RunId}/events?afterSeq=0", token);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        using var events = JsonDocument.Parse(await recovered.Content.ReadAsStringAsync(token));
        Assert.Equal(Enumerable.Range(1, 12).Select(value => (long)value),
            events.RootElement.GetProperty("events").EnumerateArray().Select(change => change.GetProperty("seq").GetInt64()));
        output.WriteLine("Real Kestrel readiness 200 -> 503; ten accepted API reads and one held request completed 200; " +
            "MessagePack reconnect hint received; physical WebSocket automatically reconnected and all twelve offline commits replayed.");
    }

    [Fact]
    public async Task A_live_client_that_never_acknowledges_cannot_hold_API_shutdown_open()
    {
        await using var fixture = await LiveResyncTests.Fixture.Start(database.DataSource, enableDrain: true);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var hub = new HubConnectionBuilder().WithUrl(new Uri(new Uri(fixture.Api), LiveHub.Path), options =>
        {
            options.AccessTokenProvider = () => Task.FromResult<string?>(fixture.Jwt);
            options.Transports = HttpTransportType.WebSockets;
            options.SkipNegotiation = true;
        }).AddMessagePackProtocol().Build();
        hub.On<LiveReconnect, bool>("Reconnect", async _ =>
        {
            received.TrySetResult();
            await release.Task;
            return true;
        });
        await hub.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var clock = Stopwatch.StartNew();
            var drain = fixture.StopApi(graceful: true);
            await received.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await drain.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"Unacknowledged-client drain took {clock.Elapsed}.");
            Assert.False(release.Task.IsCompleted);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    [Trait("Category", "Nightly")]
    public async Task Linux_SIGTERM_withdraws_actual_Program_readiness_before_a_clean_process_exit()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("WF_API_DRAIN_SIGNAL") == "true",
            "Set WF_API_DRAIN_SIGNAL=true on Linux to signal a real API-only Program process.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        var token = deadline.Token;
        using var engine = new TcpListener(IPAddress.Loopback, 0);
        using var api = new TcpListener(IPAddress.Loopback, 0);
        engine.Start();
        api.Start();
        var enginePort = ((IPEndPoint)engine.LocalEndpoint).Port;
        var apiPort = ((IPEndPoint)api.LocalEndpoint).Port;
        var root = Path.GetFullPath(Path.Combine(".agent-artifacts", "api-drain-signal-" + Guid.NewGuid().ToString("N")));
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(typeof(Migrator).Assembly.Location);
        start.ArgumentList.Add("--dev");
        start.Environment["DOTNET_ENVIRONMENT"] = "Development";
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["ConnectionStrings__Workflow"] = database.ConnectionString;
        start.Environment["Engine__Enabled"] = "false";
        start.Environment["Engine__Port"] = enginePort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["Api__Port"] = apiPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["Drain__PropagationDelay"] = "00:00:03";
        start.Environment["Storage__Root"] = root;
        start.Environment["Storage__UploadHmacKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        engine.Stop();
        api.Stop();
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the real API.");
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false })
                { BaseAddress = new Uri($"http://127.0.0.1:{apiPort}"), Timeout = TimeSpan.FromSeconds(5) };
            while (true)
            {
                if (process.HasExited)
                    Assert.Fail($"The API exited {process.ExitCode} before becoming ready.\n{await stdout}\n{await stderr}");
                try
                {
                    using var ready = await client.GetAsync("/healthz/ready", token);
                    if (ready.StatusCode == HttpStatusCode.OK) break;
                }
                catch (HttpRequestException) { }
                await Task.Delay(50, token);
            }
            var signal = new ProcessStartInfo("kill") { UseShellExecute = false };
            signal.ArgumentList.Add("-TERM");
            signal.ArgumentList.Add(process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using (var sender = Process.Start(signal) ?? throw new InvalidOperationException("Could not send SIGTERM."))
            {
                await sender.WaitForExitAsync(token);
                Assert.Equal(0, sender.ExitCode);
            }
            while (true)
            {
                using var unavailable = await client.GetAsync("/healthz/ready", token);
                if (unavailable.StatusCode == HttpStatusCode.ServiceUnavailable) break;
                Assert.Equal(HttpStatusCode.OK, unavailable.StatusCode);
                await Task.Delay(20, token);
            }
            Assert.False(process.HasExited);
            await process.WaitForExitAsync(token);
            Assert.Equal(0, process.ExitCode);
            Assert.DoesNotContain(start.Environment["Storage__UploadHmacKey"]!, await stdout + await stderr);
            output.WriteLine("Actual Linux Program: kill -TERM -> readiness 503 while still listening -> clean exit 0.");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "FrontendBrowser")]
    public async Task Graceful_API_drain_recovers_from_afterSeq_in_Chromium_Firefox_and_WebKit()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("WF_API_DRAIN_BROWSER") == "true",
            "Set WF_API_DRAIN_BROWSER=true and WF_FRONTEND_ROOT; install frontend dependencies and all Playwright engines.");
        await using var fixture = await LiveResyncTests.Fixture.Start(database.DataSource, enableDrain: true);
        await LiveResyncTests.RunBrowser(fixture, output, drain: true);
    }
}
