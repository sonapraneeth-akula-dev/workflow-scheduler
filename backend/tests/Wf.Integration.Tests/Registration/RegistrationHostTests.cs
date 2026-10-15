using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Wf.Contracts.Registration;
using Wf.Contracts.Registry;
using Wf.ControlPlane.Data;
using Wf.Integration.Tests.Agents;

namespace Wf.Integration.Tests.Registration;

public sealed class RegistrationHostTests(RegistrationHostDatabase database) : IClassFixture<RegistrationHostDatabase>
{
    [Fact]
    public async Task Actual_program_exposes_http1_registration_without_breaking_http2_engine_listener()
    {
        using var engineReservation = new TcpListener(IPAddress.Loopback, 0);
        using var apiReservation = new TcpListener(IPAddress.Loopback, 0);
        engineReservation.Start();
        apiReservation.Start();
        var enginePort = ((IPEndPoint)engineReservation.LocalEndpoint).Port;
        var apiPort = ((IPEndPoint)apiReservation.LocalEndpoint).Port;
        var root = Path.Combine(Path.GetTempPath(), "wf-registration-host-" + Guid.NewGuid().ToString("N"));
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(typeof(Migrator).Assembly.Location);
        start.ArgumentList.Add("--dev");
        start.Environment["DOTNET_ENVIRONMENT"] = "Development";
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["ConnectionStrings__Workflow"] = database.ConnectionString;
        start.Environment["Engine__Port"] = enginePort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["Api__Port"] = apiPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["Storage__Root"] = root;
        start.Environment["Storage__UploadHmacKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        engineReservation.Stop();
        apiReservation.Stop();
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        string? issuedGrant = null;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            using var client = new HttpClient { BaseAddress = new($"http://127.0.0.1:{apiPort}"), Timeout = TimeSpan.FromSeconds(3) };
            while (true)
            {
                if (process.HasExited)
                    Assert.Fail($"Control plane exited with {process.ExitCode}.\n{await stdout}\n{await stderr}");
                try
                {
                    using var ready = await client.GetAsync("/healthz", deadline.Token);
                    if (ready.IsSuccessStatusCode) break;
                }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) when (!deadline.IsCancellationRequested) { }
                await Task.Delay(50, deadline.Token);
            }
            var team = "actual-host-" + Guid.NewGuid().ToString("N");
            var registry = new TeamRegistry(team, [new(team, "release", "description", new([]))]);
            using var registered = await client.PostAsJsonAsync("/api/v1/registrations",
                new RegistrationRequest(new('a', 40), "Host integration", Registry:
                    JsonSerializer.SerializeToElement(registry, TeamRegistryJsonContext.Default.TeamRegistry)),
                RegistrationJsonContext.Default.RegistrationRequest, deadline.Token);
            Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
            Assert.Equal(HttpVersion.Version11, registered.Version);
            var package = new byte[] { 1, 2, 3, 4 };
            using var granted = await client.PostAsJsonAsync("/api/v1/upload-grants",
                new UploadGrantRequest("package", Convert.ToHexStringLower(SHA256.HashData(package)), package.Length),
                RegistrationJsonContext.Default.UploadGrantRequest, deadline.Token);
            Assert.Equal(HttpStatusCode.Created, granted.StatusCode);
            var grant = await granted.Content.ReadFromJsonAsync(RegistrationJsonContext.Default.UploadGrantResponse, deadline.Token);
            issuedGrant = grant!.UploadUri!.Split('/')[^1];
            using var uploaded = await client.PutAsync(grant.UploadUri, new ByteArrayContent(package), deadline.Token);
            Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
            using var engineRequest = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{enginePort}/healthz")
            {
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact
            };
            using var engine = await client.SendAsync(engineRequest, deadline.Token);
            Assert.Equal(HttpStatusCode.OK, engine.StatusCode);
            Assert.Equal(HttpVersion.Version20, engine.Version);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            await Task.WhenAll(stdout, stderr);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        Assert.NotNull(issuedGrant);
        var logs = await stdout + await stderr;
        Assert.DoesNotContain(issuedGrant, logs);
        Assert.DoesNotContain(start.Environment["Storage__UploadHmacKey"]!, logs);
    }
}

public sealed class RegistrationHostDatabase : AgentDatabase
{
    public string ConnectionString => DatabaseConnectionString;
    protected override IReadOnlyList<string> Migrations => Migrator.LoadScripts().Select(script => script.Name).ToArray();
}
