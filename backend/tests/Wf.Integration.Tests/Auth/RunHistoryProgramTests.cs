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
    public async Task RunHistory_and_RunNow_actual_Program_OpenApi_exporter_handles_all_new_DTOs_without_optional_immutable_arrays()
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
            deadline.CancelAfter(TimeSpan.FromSeconds(90));
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
            Assert.StartsWith("3.1.", document.RootElement.GetProperty("openapi").GetString());
            var paths = document.RootElement.GetProperty("paths");
            foreach (var path in new[] { "/api/v1/runs", "/api/v1/runs/{id}/reports", "/api/v1/audit" })
                Assert.True(paths.GetProperty(path).TryGetProperty("get", out _), $"Public read missing: {path}");
            Assert.True(paths.GetProperty("/api/v1/schedules/{id}").TryGetProperty("get", out _));
            Assert.True(paths.GetProperty("/api/v1/schedules").TryGetProperty("get", out _));
            Assert.False(paths.GetProperty("/api/v1/schedules").TryGetProperty("post", out _));
            Assert.True(paths.GetProperty("/api/v1/permissions/{scope}").TryGetProperty("get", out _));
            Assert.True(paths.GetProperty("/api/v1/series/{id}/fires").TryGetProperty("get", out _));
            Assert.False(paths.GetProperty("/api/v1/schedules/{id}").TryGetProperty("put", out _));
            Assert.False(paths.GetProperty("/api/v1/schedules/{id}").TryGetProperty("delete", out _));
            foreach (var path in new[] { "/api/v1/schedules/preview",
                "/api/v1/schedules/{id}/pause", "/api/v1/schedules/{id}/resume" })
                Assert.False(paths.TryGetProperty(path, out _), $"UI mutation leaked into public OpenAPI: {path}");
            using var uiResponse = await client.GetAsync("/openapi/ui.json", deadline.Token);
            Assert.Equal(HttpStatusCode.OK, uiResponse.StatusCode);
            using var ui = JsonDocument.Parse(await uiResponse.Content.ReadAsStringAsync(deadline.Token));
            Assert.StartsWith("3.1.", ui.RootElement.GetProperty("openapi").GetString());
            var internalPaths = ui.RootElement.GetProperty("paths");
            foreach (var (path, method) in new[] { ("/api/v1/schedules", "post"), ("/api/v1/schedules/{id}", "put"),
                ("/api/v1/schedules/{id}", "delete"), ("/api/v1/schedules/preview", "post"),
                ("/api/v1/schedules/{id}/pause", "post"), ("/api/v1/schedules/{id}/resume", "post") })
                Assert.True(internalPaths.GetProperty(path).TryGetProperty(method, out _), $"UI operation missing: {method} {path}");
            Assert.False(internalPaths.GetProperty("/api/v1/schedules/{id}").TryGetProperty("get", out _));
            foreach (var path in new[] { "/api/v1/runs", "/api/v1/runs/{id}/reports", "/api/v1/audit" })
                Assert.False(internalPaths.TryGetProperty(path, out _), $"Public read leaked into UI-only document: {path}");
            var readiness = paths.GetProperty("/healthz/ready").GetProperty("get").GetProperty("responses");
            Assert.True(readiness.TryGetProperty("200", out _));
            Assert.True(readiness.TryGetProperty("503", out _));
            Assert.True(paths.GetProperty("/api/v1/runs/history").TryGetProperty("get", out _));
            Assert.True(paths.GetProperty("/api/v1/series/{id}/history").TryGetProperty("get", out _));
            Assert.True(paths.GetProperty("/api/v1/workflows/{team}/{type}/{workflow}/history").TryGetProperty("get", out _));
            var rerun = paths.GetProperty("/api/v1/runs/{id}/rerun").GetProperty("post");
            Assert.Equal("#/components/schemas/RerunRunRequest", rerun.GetProperty("requestBody")
                .GetProperty("content").GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
            Assert.Equal("#/components/schemas/TriggerRunResponse", rerun.GetProperty("responses").GetProperty("202")
                .GetProperty("content").GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
            Assert.True(rerun.GetProperty("responses").TryGetProperty("409", out _));
            Assert.Contains(rerun.GetProperty("parameters").EnumerateArray(), parameter =>
                parameter.GetProperty("name").GetString() == "Idempotency-Key");
            Assert.Contains(rerun.GetProperty("parameters").EnumerateArray(), parameter =>
                parameter.GetProperty("name").GetString() == "id" &&
                parameter.GetProperty("in").GetString() == "path" &&
                parameter.GetProperty("required").GetBoolean());
            var activation = paths.GetProperty("/api/v1/workflows/{team}/{type}/{workflow}/versions/{version}/activate")
                .GetProperty("post");
            Assert.False(activation.TryGetProperty("requestBody", out _));
            Assert.Equal("#/components/schemas/WorkflowVersionActivation",
                activation.GetProperty("responses").GetProperty("200").GetProperty("content")
                    .GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
            Assert.True(activation.GetProperty("responses").TryGetProperty("403", out _));
            Assert.True(activation.GetProperty("responses").TryGetProperty("409", out _));
            var runNow = paths.GetProperty("/api/v1/schedules/{id}/run-now").GetProperty("post");
            Assert.Contains(runNow.GetProperty("parameters").EnumerateArray(), parameter =>
                parameter.GetProperty("name").GetString() == "id" &&
                parameter.GetProperty("in").GetString() == "path" &&
                parameter.GetProperty("required").GetBoolean());
            Assert.Equal("#/components/schemas/ScheduleRunNowResponse", runNow.GetProperty("responses").GetProperty("202")
                .GetProperty("content").GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
            var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
            var runSnapshot = schemas.GetProperty("RunSnapshotResponse");
            Assert.True(runSnapshot.GetProperty("properties").TryGetProperty("parentRunId", out _));
            Assert.True(runSnapshot.GetProperty("properties").TryGetProperty("reusedNodes", out _));
            Assert.Contains(runSnapshot.GetProperty("required").EnumerateArray(), property => property.GetString() == "parentRunId");
            Assert.Contains(runSnapshot.GetProperty("required").EnumerateArray(), property => property.GetString() == "reusedNodes");
            Assert.True(schemas.GetProperty("LiveReconnect").GetProperty("properties").TryGetProperty("reason", out _));
            var machines = paths.GetProperty("/api/v1/machines").GetProperty("get");
            Assert.Equal("#/components/schemas/MachineListResponse", machines.GetProperty("responses").GetProperty("200")
                .GetProperty("content").GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
            Assert.Equal(["label", "os", "pool", "status"], machines.GetProperty("parameters").EnumerateArray()
                .Select(parameter => parameter.GetProperty("name").GetString()).Order());
            Assert.True(paths.GetProperty("/api/v1/machines/{id}").GetProperty("patch").GetProperty("responses").TryGetProperty("204", out _));
            Assert.True(schemas.GetProperty("MachineResponse").GetProperty("properties").TryGetProperty("currentReservation", out _));
            Assert.True(schemas.TryGetProperty("RunHistoryPage", out _));
            Assert.Equal("#/components/schemas/ReservationQueueResponse",
                paths.GetProperty("/api/v1/runs/{id}/reservation-queue").GetProperty("get")
                    .GetProperty("responses").GetProperty("200").GetProperty("content").GetProperty("application/json")
                    .GetProperty("schema").GetProperty("$ref").GetString());
            Assert.True(schemas.GetProperty("ReservationQueueItem").GetProperty("properties").TryGetProperty("blockingRunId", out _));
            Assert.True(schemas.GetProperty("RunQueryRow").GetProperty("properties").TryGetProperty("metadata", out _));
            Assert.Contains(schemas.GetProperty("RunQueryRow").GetProperty("required").EnumerateArray(),
                property => property.GetString() == "metadata");
            Assert.Contains(schemas.GetProperty("RunQueryRow").GetProperty("properties").GetProperty("metadata")
                .GetProperty("oneOf").EnumerateArray(), option => option.TryGetProperty("type", out var type) && type.GetString() == "null");
            output.WriteLine("Actual Program RunQueryRow OpenAPI: " + schemas.GetProperty("RunQueryRow").GetRawText());
            using var query = await client.GetAsync("/api/v1/runs/history", deadline.Token);
            Assert.Equal(HttpStatusCode.BadRequest, query.StatusCode);
            Assert.True(query.Headers.CacheControl!.NoStore);
            if (Environment.GetEnvironmentVariable("WF_FRONTEND_API_CHECK") is { } frontend)
            {
                var commands = Environment.GetEnvironmentVariable("WF_FRONTEND_API_GENERATE") == "true"
                    ? new[] { "api:generate", "api:check" } : ["api:check"];
                foreach (var command in commands)
                {
                    var generate = new ProcessStartInfo("bun")
                    {
                        WorkingDirectory = frontend, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
                    };
                    generate.ArgumentList.Add("run");
                    generate.ArgumentList.Add(command);
                    generate.Environment["KARDIA_API_URL"] = client.BaseAddress!.AbsoluteUri.TrimEnd('/');
                    generate.Environment.Remove("KARDIA_API_TOKEN");
                    using var generated = Process.Start(generate)!;
                    var generatedOut = generated.StandardOutput.ReadToEndAsync(token);
                    var generatedError = generated.StandardError.ReadToEndAsync(token);
                    try
                    {
                        await generated.WaitForExitAsync(deadline.Token);
                        Assert.True(generated.ExitCode == 0, $"API contract drift check failed.\n{await generatedOut}\n{await generatedError}");
                    }
                    finally
                    {
                        if (!generated.HasExited) generated.Kill(entireProcessTree: true);
                        await generated.WaitForExitAsync(CancellationToken.None);
                        await Task.WhenAll(generatedOut, generatedError);
                    }
                }
            }
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
