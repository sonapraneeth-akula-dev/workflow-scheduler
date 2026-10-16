using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Wf.ControlPlane.Data;
using Wf.Integration.Tests.Registration;

namespace Wf.Integration.Tests.Auth;

public sealed class RunHistoryProgramTests(RegistrationHostDatabase database, ITestOutputHelper output) : IClassFixture<RegistrationHostDatabase>
{
    [Fact]
    public async Task RunHistory_actual_Program_OpenApi_exporter_handles_all_new_DTOs_without_optional_immutable_arrays()
    {
        var token = TestContext.Current.CancellationToken;
        using var engine = new TcpListener(IPAddress.Loopback, 0);
        using var api = new TcpListener(IPAddress.Loopback, 0);
        engine.Start();
        api.Start();
        var enginePort = ((IPEndPoint)engine.LocalEndpoint).Port;
        var apiPort = ((IPEndPoint)api.LocalEndpoint).Port;
        var root = Path.GetFullPath(Path.Combine(".agent-artifacts", "run-history-program-" + Guid.NewGuid().ToString("N")));
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
        engine.Stop();
        api.Stop();
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(token);
        var stderr = process.StandardError.ReadToEndAsync(token);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(45));
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false })
                { BaseAddress = new($"http://127.0.0.1:{apiPort}"), Timeout = TimeSpan.FromSeconds(10) };
            while (true)
            {
                if (process.HasExited) Assert.Fail($"Program exited {process.ExitCode}.\n{await stdout}\n{await stderr}");
                try
                {
                    using var response = await client.GetAsync("/healthz", deadline.Token);
                    if (response.IsSuccessStatusCode) break;
                }
                catch (HttpRequestException) { }
                await Task.Delay(50, deadline.Token);
            }
            using var exported = await client.GetAsync("/openapi/v1.json", deadline.Token);
            Assert.True(exported.StatusCode == HttpStatusCode.OK, await exported.Content.ReadAsStringAsync(deadline.Token));
            using var document = JsonDocument.Parse(await exported.Content.ReadAsStringAsync(deadline.Token));
            var paths = document.RootElement.GetProperty("paths");
            Assert.True(paths.GetProperty("/api/v1/runs/history").TryGetProperty("get", out _));
            Assert.True(paths.GetProperty("/api/v1/series/{id}/history").TryGetProperty("get", out _));
            Assert.True(paths.GetProperty("/api/v1/workflows/{team}/{type}/{workflow}/history").TryGetProperty("get", out _));
            var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
            Assert.True(schemas.TryGetProperty("RunHistoryPage", out _));
            Assert.True(schemas.GetProperty("RunQueryRow").GetProperty("properties").TryGetProperty("metadata", out _));
            Assert.Contains(schemas.GetProperty("RunQueryRow").GetProperty("required").EnumerateArray(),
                property => property.GetString() == "metadata");
            Assert.Contains(schemas.GetProperty("RunQueryRow").GetProperty("properties").GetProperty("metadata")
                .GetProperty("oneOf").EnumerateArray(), option => option.TryGetProperty("type", out var type) && type.GetString() == "null");
            output.WriteLine("Actual Program RunQueryRow OpenAPI: " + schemas.GetProperty("RunQueryRow").GetRawText());
            using var query = await client.GetAsync("/api/v1/runs/history", deadline.Token);
            Assert.Equal(HttpStatusCode.BadRequest, query.StatusCode);
            Assert.True(query.Headers.CacheControl!.NoStore);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        Assert.DoesNotContain(start.Environment["Storage__UploadHmacKey"]!, await stdout + await stderr);
    }
}
