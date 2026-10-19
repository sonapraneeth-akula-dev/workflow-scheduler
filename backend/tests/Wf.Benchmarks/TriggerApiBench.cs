#if !ENGINE_BASELINE
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;
using Wf.Contracts.Registration;
using Wf.Contracts.Registry;
using Wf.Contracts.Runs;
using Wf.ControlPlane.Api;
using Wf.ControlPlane.Auth;
using Wf.ControlPlane.Data;
using Wf.ControlPlane.Engine.Recovery;
using Wf.ControlPlane.Engine.Registry;
using Wf.ControlPlane.Registration;
using Wf.ControlPlane.Storage;
using Wf.Sdk;

namespace Wf.Benchmarks;

public static class TriggerApiBench
{
    private const int RequestsPerSecond = 50;
    private const int WarmupCount = 50;
    private const int SampleCount = 500;
    private const string WorkflowId = "trigger-bench/release/tiny";
    private const string Actor = "00000000-0000-0000-0000-000000000001";

    public static async Task<int> RunAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        ConsoleCancelEventHandler cancel = (_, args) => { args.Cancel = true; timeout.Cancel(); };
        Console.CancelKeyPress += cancel;
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        try
        {
            var (metrics, passed) = await MeasureAsync(timeout.Token);
            Console.WriteLine(JsonSerializer.Serialize(metrics, json));
            return passed ? 0 : 1;
        }
        catch (Exception error)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { benchmark = nameof(TriggerApiBench), passed = false, error = error.Message }, json));
            Console.Error.WriteLine(error);
            return 1;
        }
        finally { Console.CancelKeyPress -= cancel; }
    }

    private static async Task<(object Metrics, bool Passed)> MeasureAsync(CancellationToken token)
    {
        PostgreSqlContainer? postgres = null;
        NpgsqlDataSource? source = null;
        WebApplication? app = null;
        string? schema = null;
        var external = Environment.GetEnvironmentVariable("WF_TEST_POSTGRES");
        var databaseMode = external is null ? "isolated postgres:18.6-alpine container" : "WF_TEST_POSTGRES unique schema";
        var blobRoot = Path.GetFullPath(Path.Combine(".agent-artifacts", "BenchmarkDotNet.Artifacts", $"trigger-api-{Guid.NewGuid():N}"));
        try
        {
            if (external is null)
            {
                postgres = new PostgreSqlBuilder("postgres:18.6-alpine")
                    .WithName($"kardia-bench-trigger-api-postgres-{Guid.NewGuid().ToString("N")[..12]}")
                    .WithCommand("-c", "fsync=on", "-c", "full_page_writes=on", "-c", "synchronous_commit=on").Build();
                await postgres.StartAsync(token);
                external = postgres.GetConnectionString();
            }
            schema = $"trigger_api_{Guid.NewGuid():N}";
            await using (var admin = new NpgsqlConnection(external))
            {
                await admin.OpenAsync(token);
                await using var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", admin);
                await create.ExecuteNonQueryAsync(token);
            }
            var connectionString = new NpgsqlConnectionStringBuilder(external) { SearchPath = schema }.ConnectionString;
            token.ThrowIfCancellationRequested();
            Migrator.Upgrade(connectionString, NullLogger.Instance, Migrator.LoadScripts());
            source = NpgsqlDataSource.Create(connectionString);
            var durability = new Dictionary<string, string>();
            string postgresVersion;
            await using (var query = source.CreateCommand("""
                SELECT current_setting('fsync'),current_setting('full_page_writes'),
                    current_setting('synchronous_commit'),version()
                """))
            await using (var reader = await query.ExecuteReaderAsync(token))
            {
                if (!await reader.ReadAsync(token)) throw new InvalidOperationException("PostgreSQL settings are missing.");
                durability["fsync"] = reader.GetString(0);
                durability["full_page_writes"] = reader.GetString(1);
                durability["synchronous_commit"] = reader.GetString(2);
                postgresVersion = reader.GetString(3);
            }
            if (durability.Values.Any(value => value != "on"))
                throw new InvalidOperationException("Trigger benchmark requires fsync, full_page_writes and synchronous_commit ON.");

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development, Args = [] });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            builder.Configuration["Authentication:Mode"] = "Development";
            builder.Configuration["Storage:Root"] = blobRoot;
            builder.Configuration["Storage:UploadHmacKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            builder.Services.AddWorkflowAuthentication(builder.Configuration, builder.Environment);
            builder.Services.AddSingleton(source);
            builder.Services.AddTeamAuthorization();
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddWorkflowRegistrations(builder.Configuration);
            builder.Services.AddSingleton<CommandInbox>();
            builder.Services.AddSingleton<ActiveVersionCache>();
            builder.Services.AddHostedService<ActiveVersionCache>(services => services.GetRequiredService<ActiveVersionCache>());
            app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapRunsEndpoints();
            var version = await RegisterAsync(app.Services, token);
            await app.StartAsync(token);
            var versions = app.Services.GetRequiredService<ActiveVersionCache>();
            while (!versions.IsReady) await Task.Delay(10, token);
            if (versions.GetActive(WorkflowId).VersionId != version)
                throw new InvalidOperationException("Hosted active-version cache did not load the registered version.");
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, MaxConnectionsPerServer = SampleCount };
            using var client = new HttpClient(handler)
            {
                BaseAddress = new Uri(address),
                Timeout = TimeSpan.FromSeconds(10),
                DefaultRequestVersion = HttpVersion.Version11,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
            };
            var body = JsonSerializer.SerializeToUtf8Bytes(new TriggerRunRequest(WorkflowId), RunJsonContext.Default.TriggerRunRequest);
            var unkeyed = await PostAsync(client, body, null, Stopwatch.GetTimestamp(), token);
            var replayKey = Guid.NewGuid().ToString("N");
            var first = await PostAsync(client, body, replayKey, Stopwatch.GetTimestamp(), token);
            var replay = await PostAsync(client, body, replayKey, Stopwatch.GetTimestamp(), token);
            if (first.RunId != replay.RunId) throw new InvalidOperationException("Idempotency replay changed the run ID.");
            await VerifyPersistenceAsync(source, [unkeyed, first], version, token);

            var warmup = await LoadAsync(client, body, WarmupCount, token);
            var measured = await LoadAsync(client, body, SampleCount, token);
            var (commandCount, keyCount) = await VerifyPersistenceAsync(source,
                [unkeyed, first, .. warmup.Samples, .. measured.Samples], version, token);
            var latencies = measured.Samples.Select(sample => sample.LatencyMs).Order().ToArray();
            var lags = measured.Samples.Select(sample => sample.StartLagMs).Order().ToArray();
            var p95 = Percentile(latencies, 0.95);
            var passed = latencies.All(value => double.IsFinite(value) && value > 0) &&
                lags.All(value => double.IsFinite(value) && value >= 0) && double.IsFinite(p95) && p95 < 100;
            return (new
            {
                benchmark = nameof(TriggerApiBench),
                passed,
                environment = new
                {
                    os = RuntimeInformation.OSDescription,
                    framework = RuntimeInformation.FrameworkDescription,
                    architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                    processors = Environment.ProcessorCount,
                    serverGc = GCSettings.IsServerGC,
                    stopwatchFrequency = Stopwatch.Frequency,
                    postgres = postgresVersion,
                    databaseMode,
                    authentication = "Development + AddTeamAuthorization (real database resource lookup)",
                    transport = "Kestrel loopback HTTP/1.1",
                    clock = "TimeProvider.System"
                },
                durability,
                sampleCount = latencies.Length,
                warmupCount = WarmupCount,
                scheduledWindowSeconds = (double)SampleCount / RequestsPerSecond,
                measuredDurationSeconds = measured.DurationSeconds,
                offeredRequestsPerSecond = RequestsPerSecond,
                achievedRequestsPerSecond = SampleCount / measured.DurationSeconds,
                dispatchedRequestsPerSecond = (SampleCount - 1) /
                    Stopwatch.GetElapsedTime(measured.Samples[0].Started, measured.Samples[^1].Started).TotalSeconds,
                httpLatencyMs = new { p50 = Percentile(latencies, 0.50), p95, p99 = Percentile(latencies, 0.99) },
                arrivalStartLagMs = new { p50 = Percentile(lags, 0.50), p95 = Percentile(lags, 0.95), p99 = Percentile(lags, 0.99), max = lags[^1] },
                gate = "finite HTTP latency p95 < 100 ms; every response 202/UUIDv7; exact durable commands and idempotency responses",
                replayVerified = true,
                unkeyedTriggerVerified = true,
                persistedCommands = commandCount,
                expectedCommands = 2 + WarmupCount + SampleCount,
                persistedIdempotencyKeys = keyCount,
                expectedIdempotencyKeys = 1 + WarmupCount + SampleCount
            }, passed);
        }
        finally { await CleanupAsync(app, source, external, schema, postgres, blobRoot); }
    }

    private static async Task<Guid> RegisterAsync(IServiceProvider services, CancellationToken token)
    {
        var workflow = Workflow.Define(WorkflowId, "1.0.0", builder =>
            builder.Type("release").Node("work", node => node.Task("task", _ => { })));
        var manifestBytes = ManifestWriter.WriteUtf8(workflow);
        using var manifest = JsonDocument.Parse(manifestBytes);
        using var package = new MemoryStream();
        using (var zip = new ZipArchive(package, ZipArchiveMode.Create, leaveOpen: true))
        {
            await using var entry = zip.CreateEntry("manifest.json").Open();
            await entry.WriteAsync(manifestBytes, token);
        }
        var bytes = package.ToArray();
        var content = new BlobContent(BlobKind.Packages, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length);
        package.Position = 0;
        await services.GetRequiredService<IBlobStore>().UploadAsync(content, package, token);
        var registry = new TeamRegistry("trigger-bench", [new("trigger-bench", "release", "Trigger API benchmark", new([]))]);
        var response = await services.GetRequiredService<RegistrationService>().RegisterAsync(
            new RegistrationRequest(new string('a', 40), "Trigger API benchmark", WorkflowId, manifest.RootElement,
                new PackageReference(content.Sha256, content.SizeBytes), "// tiny SDK workflow",
                JsonSerializer.SerializeToElement(registry, TeamRegistryJsonContext.Default.TeamRegistry), "Benchmark setup"), Actor, token);
        return response.WorkflowVersion ?? throw new InvalidOperationException("Benchmark workflow was not registered.");
    }

    private static async Task<Load> LoadAsync(HttpClient client, byte[] body, int count, CancellationToken token)
    {
        var requests = new List<Task<Sample>>(count);
        var origin = Stopwatch.GetTimestamp();
        Sample[] samples;
        try
        {
            for (var index = 0; index < count; index++)
            {
                var scheduled = origin + (long)((double)index * Stopwatch.Frequency / RequestsPerSecond);
                await WaitUntilAsync(scheduled, token);
                // Dispatch independently of prior responses: slow requests never lower the offered rate.
                requests.Add(PostAsync(client, body, Guid.NewGuid().ToString("N"), scheduled, token));
            }
            await WaitUntilAsync(origin + (long)((double)count * Stopwatch.Frequency / RequestsPerSecond), token);
        }
        finally { samples = await Task.WhenAll(requests); }
        return new(samples, Stopwatch.GetElapsedTime(origin).TotalSeconds);
    }

    private static async Task WaitUntilAsync(long scheduled, CancellationToken token)
    {
        while (scheduled > Stopwatch.GetTimestamp())
        {
            token.ThrowIfCancellationRequested();
            var remaining = (double)(scheduled - Stopwatch.GetTimestamp()) / Stopwatch.Frequency;
            if (remaining > 0) await Task.Delay(TimeSpan.FromSeconds(remaining), token);
        }
        token.ThrowIfCancellationRequested();
    }

    private static async Task<Sample> PostAsync(HttpClient client, byte[] body, string? key, long scheduled, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/runs") { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new("application/json");
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        var started = Stopwatch.GetTimestamp();
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, token);
        var latency = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (response.StatusCode != HttpStatusCode.Accepted)
            throw new InvalidOperationException($"Trigger returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(token)}");
        var result = JsonSerializer.Deserialize(await response.Content.ReadAsByteArrayAsync(token), RunJsonContext.Default.TriggerRunResponse);
        if (result is null || result.RunId == Guid.Empty || result.RunId.Version != 7)
            throw new InvalidOperationException("Accepted trigger did not return a UUIDv7 run ID.");
        return new(result.RunId, key, latency, Stopwatch.GetElapsedTime(scheduled, started).TotalMilliseconds, started);
    }

    private static async Task<(int Commands, int Keys)> VerifyPersistenceAsync(NpgsqlDataSource source, Sample[] samples,
        Guid version, CancellationToken token)
    {
        var expected = samples.Select(sample => sample.RunId).ToHashSet();
        if (expected.Count != samples.Length) throw new InvalidOperationException("Fresh trigger requests returned duplicate run IDs.");
        var actual = new HashSet<Guid>();
        await using (var query = source.CreateCommand("SELECT run_id,kind,payload->>'WorkflowId',payload->>'VersionId',created_by FROM commands"))
        await using (var reader = await query.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token))
                if (!actual.Add(reader.GetGuid(0)) || reader.GetInt16(1) != 0 || reader.GetString(2) != WorkflowId ||
                    !Guid.TryParse(reader.GetString(3), out var persistedVersion) || persistedVersion != version || reader.GetString(4) != Actor)
                    throw new InvalidOperationException("Persisted trigger command does not match the registered workflow/principal/version.");
        if (!actual.SetEquals(expected)) throw new InvalidOperationException("Durable command count/run IDs do not match accepted triggers.");
        var expectedKeys = samples.Where(sample => sample.Key is not null).ToDictionary(sample => sample.Key!, sample => sample.RunId);
        var keyCount = 0;
        await using (var query = source.CreateCommand("SELECT key,status_code,response->>'runId' FROM idempotency_keys"))
        await using (var reader = await query.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token))
            {
                if (!expectedKeys.TryGetValue(reader.GetString(0), out var runId) || reader.IsDBNull(1) || reader.GetInt32(1) != 202 ||
                    reader.IsDBNull(2) || !Guid.TryParse(reader.GetString(2), out var persistedRun) || persistedRun != runId)
                    throw new InvalidOperationException("Durable idempotency response does not match the accepted key/run ID.");
                keyCount++;
            }
        if (keyCount != expectedKeys.Count) throw new InvalidOperationException("Durable idempotency key count does not match accepted keyed triggers.");
        return (actual.Count, keyCount);
    }

    private static async Task CleanupAsync(WebApplication? app, NpgsqlDataSource? source, string? connectionString,
        string? schema, PostgreSqlContainer? postgres, string blobRoot)
    {
        var failures = new List<Exception>();
        async Task Attempt(Func<Task> action)
        {
            try { await action(); }
            catch (Exception error) { failures.Add(error); }
        }
        if (app is not null)
        {
            await Attempt(async () =>
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await app.StopAsync(timeout.Token);
            });
            await Attempt(() => app.DisposeAsync().AsTask());
        }
        if (source is not null) await Attempt(() => source.DisposeAsync().AsTask());
        if (schema is not null && connectionString is not null)
            await Attempt(async () =>
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await using var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString)
                    { Pooling = false, Timeout = 10, CommandTimeout = 15 }.ConnectionString);
                await admin.OpenAsync(timeout.Token);
                await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", admin);
                await drop.ExecuteNonQueryAsync(timeout.Token);
            });
        if (postgres is not null) await Attempt(() => postgres.DisposeAsync().AsTask());
        await Attempt(() =>
        {
            if (Directory.Exists(blobRoot)) Directory.Delete(blobRoot, recursive: true);
            return Task.CompletedTask;
        });
        if (failures.Count > 0) throw new AggregateException("Trigger benchmark cleanup failed.", failures);
    }

    private static double Percentile(double[] sorted, double percentile) => sorted[(int)Math.Ceiling(sorted.Length * percentile) - 1];
    private sealed record Sample(Guid RunId, string? Key, double LatencyMs, double StartLagMs, long Started);
    private sealed record Load(Sample[] Samples, double DurationSeconds);
}
#endif
