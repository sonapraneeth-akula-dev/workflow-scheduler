using System.Collections.Immutable;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Wf.Contracts.Definition;
using Wf.ControlPlane.Api;
using Wf.ControlPlane.Data;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Registry;
using Wf.Engine.Core;

namespace Wf.Integration.Tests.Execution;

public sealed class RunTimelineProgramTests(RunTimelineDatabase database, ITestOutputHelper output)
    : IClassFixture<RunTimelineDatabase>
{
    [Fact]
    public async Task Timeline_actual_Program_maps_route_sourcegen_and_OpenAPI_without_optional_array_export_failure()
    {
        var token = TestContext.Current.CancellationToken;
        using var engine = new TcpListener(IPAddress.Loopback, 0);
        using var api = new TcpListener(IPAddress.Loopback, 0);
        engine.Start();
        api.Start();
        var enginePort = ((IPEndPoint)engine.LocalEndpoint).Port;
        var apiPort = ((IPEndPoint)api.LocalEndpoint).Port;
        var root = RunTimelineApiTests.ArtifactRoot();
        var storage = Path.Combine(root, "program-storage-" + Guid.NewGuid().ToString("N"));
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = root
        };
        start.ArgumentList.Add(typeof(Migrator).Assembly.Location);
        start.ArgumentList.Add("--dev");
        start.Environment["DOTNET_ENVIRONMENT"] = "Development";
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["ConnectionStrings__Workflow"] = database.ConnectionString;
        start.Environment["Engine__Port"] = enginePort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["Api__Port"] = apiPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["Storage__Root"] = storage;
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
                    using var health = await client.GetAsync("/healthz", deadline.Token);
                    if (health.IsSuccessStatusCode) break;
                }
                catch (HttpRequestException) { }
                await Task.Delay(50, deadline.Token);
            }
            using var exported = await client.GetAsync("/openapi/v1.json", deadline.Token);
            var text = await exported.Content.ReadAsStringAsync(deadline.Token);
            Assert.True(exported.StatusCode == HttpStatusCode.OK, text);
            using var document = JsonDocument.Parse(text);
            RunTimelineApiTests.AssertTimelineSchema(document.RootElement);
            await File.WriteAllTextAsync(Path.Combine(root, "timeline-program-openapi.json"), text, token);
            var team = "timeline-program-" + Guid.NewGuid().ToString("N");
            await using (var connection = await database.DataSource.OpenConnectionAsync(token))
            await using (var transaction = await connection.BeginTransactionAsync(token))
            {
                await new RegistryWriter().UpsertAsync(connection, transaction,
                    new(team, [new(team, "metrics", "Timeline Program", new([]))]), token);
                await transaction.CommitAsync(token);
            }
            var run = new RunState(Guid.CreateVersion7(), [new("node", [], [new("task")])],
                new WorkflowMetadata("metrics", ImmutableSortedDictionary<string, string>.Empty), Assert.Fail);
            await new RunStore(database.DataSource, NullLogger<RunStore>.Instance).CreateAsync(run,
                $"{team}/metrics/workflow", Guid.CreateVersion7(), DateTimeOffset.UtcNow.AddHours(1), token);
            using var response = await client.GetAsync($"/api/v1/runs/{run.Snapshot.RunId:D}/timeline", deadline.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            var timeline = JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(deadline.Token),
                RunTimelineJsonContext.Default.RunTimelineResponse)!;
            Assert.Equal(run.Snapshot.RunId, timeline.RunId);
            Assert.Empty(timeline.Attempts);
            using var invalid = await client.GetAsync("/api/v1/runs/invalid/timeline", deadline.Token);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.True(invalid.Headers.CacheControl?.NoStore);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
            if (Directory.Exists(storage)) Directory.Delete(storage, recursive: true);
        }
        Assert.DoesNotContain(start.Environment["Storage__UploadHmacKey"]!, await stdout + await stderr);
        output.WriteLine("Actual Program timeline route and OpenAPI verified; owned process/storage cleaned.");
    }
}
