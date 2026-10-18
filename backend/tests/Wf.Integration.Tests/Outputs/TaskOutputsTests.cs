using System.Collections.Immutable;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Wf.Agent;
using Wf.Agent.Packages;
using Wf.Contracts;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Events;
using Wf.Contracts.Outputs;
using Wf.ControlPlane.Api;
using Wf.ControlPlane.Auth;
using Wf.ControlPlane.Data;
using Wf.ControlPlane.Engine.Agents;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine;
using Wf.ControlPlane.Storage;
using Wf.Engine.Core;
using Wf.Engine.Core.Placement;
using Wf.Integration.Tests.Agents;
using Wf.TaskHost.Fixture;

namespace Wf.Integration.Tests.Outputs;

public sealed class TaskOutputsTests(TaskOutputsDatabase database, ITestOutputHelper output) : IClassFixture<TaskOutputsDatabase>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TaskOutputs_actual_process_agent_SDK_gRPC_Postgres_HTTP_and_retry_history()
    {
        await using var host = await OutputHost.Start(database.DataSource);
        var package = await Package();
        host.App.MapGet("/package.zip", () => Results.Bytes(package, "application/zip"));
        var agentId = Guid.NewGuid();
        using var agent = StartAgent(host, agentId);
        var stdout = agent.StandardOutput.ReadToEndAsync(Token);
        var stderr = agent.StandardError.ReadToEndAsync(Token);
        try
        {
            await Wait(() => Task.FromResult(host.Registry.Find(agentId) is not null));
            var session = new AgentSession(agentId, host.Registry.Find(agentId)!.SessionId);
            var success = await host.Seed(session, "outputs", package);
            var id = success.Snapshot.RunId;
            await Wait(async () => (await host.Runs.LoadAsync(id, Token)).Snapshot.Tasks["work/outputs"] == ExecutionStatus.Succeeded);
            var during = await host.Read(id, "outputs");
            Assert.Equal(id, during.RunId);
            Assert.Equal("work/outputs", during.TaskKey);
            Assert.Equal("<script>window.outputExecuted=true</script>", during.Outputs!.Value.GetProperty("text").GetString());
            var pid = during.Outputs.Value.GetProperty("pid").GetInt32();
            Assert.NotEqual(Environment.ProcessId, pid);
            Assert.NotEqual(agent.Id, pid);
            Assert.Equal(JsonValueKind.Null, during.Outputs.Value.GetProperty("null").ValueKind);
            var reloaded = await new RunStore(database.DataSource, NullLogger<RunStore>.Instance).LoadAsync(id, Token);
            Assert.Equal(RunStatus.Running, reloaded.Snapshot.Status);
            await host.Runs.AppendAsync(reloaded, EventEntity.Node, "work", (int)ExecutionStatus.Succeeded, DateTimeOffset.UtcNow, Token);
            await host.Runs.AppendAsync(reloaded, EventEntity.Run, null, (int)RunStatus.Succeeded, DateTimeOffset.UtcNow, Token);
            Assert.Equal(during.Outputs.Value.GetRawText(), (await host.Read(id, "outputs")).Outputs!.Value.GetRawText());
            Assert.Contains("window.outputExecuted", await host.Scalar(id, "SELECT outputs::text FROM node_runs WHERE run_id=$1"));

            var failed = await host.Seed(session, "failedOutputs", package);
            var failedId = failed.Snapshot.RunId;
            await Wait(async () => (await host.Runs.LoadAsync(failedId, Token)).Snapshot.Tasks["work/failedOutputs"] == ExecutionStatus.Failed);
            var first = await host.Read(failedId, "failedOutputs");
            Assert.Equal(1, first.Outputs!.Value.GetProperty("partial").GetInt32());
            // Retry planning is outside this boundary test. Seed its Ready snapshot, retaining the real completed attempt metrics.
            await host.Execute("""
                UPDATE run_snapshots s SET payload=convert_to(jsonb_set(convert_from(payload,'UTF8')::jsonb,
                    '{Tasks,work/failedOutputs}','1'::jsonb)::text,'UTF8')
                FROM runs r WHERE r.id=$1 AND s.run_id=r.id AND s.seq=r.snapshot_seq
                """, failedId);
            failed = await host.Runs.LoadAsync(failedId, Token);
            await host.Dispatch(failed, session, "failedOutputs", 2, package);
            await Wait(async () => (await host.Runs.LoadAsync(failedId, Token)).Snapshot.Metrics!.Tasks["work/failedOutputs"].Attempts.Length == 2 &&
                (await host.Runs.LoadAsync(failedId, Token)).Snapshot.Tasks["work/failedOutputs"] == ExecutionStatus.Failed);
            Assert.Equal(2, (await host.Read(failedId, "failedOutputs", 2)).Outputs!.Value.GetProperty("partial").GetInt32());
            Assert.Equal(first.Outputs.Value.GetRawText(), (await host.Read(failedId, "failedOutputs")).Outputs!.Value.GetRawText());
            Assert.Null(await host.Scalar(failedId, "SELECT outputs::text FROM node_runs WHERE run_id=$1"));
            if (Environment.GetEnvironmentVariable("WF_TASK_OUTPUTS_EXPORT_FIXTURE") is { } export)
            {
                var snapshot = await host.Scalar(failedId, """
                    SELECT convert_from(s.payload,'UTF8') FROM run_snapshots s JOIN runs r ON r.id=s.run_id AND r.snapshot_seq=s.seq WHERE r.id=$1
                    """);
                await using var stream = File.Create(export);
                using var writer = new Utf8JsonWriter(stream);
                writer.WriteStartObject();
                writer.WritePropertyName("snapshot");
                writer.WriteRawValue(snapshot!);
                writer.WriteStartArray("outputs");
                foreach (var number in new[] { 1, 2 })
                    writer.WriteRawValue((await host.Read(failedId, "failedOutputs", number)).Outputs!.Value.GetRawText());
                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            var oversize = await host.Seed(session, "oversizedOutputs", package);
            await Wait(async () => (await host.Runs.LoadAsync(oversize.Snapshot.RunId, Token)).Snapshot.Tasks["work/oversizedOutputs"] == ExecutionStatus.Failed);
            Assert.Contains("65536", await host.Scalar(oversize.Snapshot.RunId, "SELECT error FROM task_attempts WHERE run_id=$1"));
            Assert.Equal("{}", (await host.Read(oversize.Snapshot.RunId, "oversizedOutputs")).Outputs!.Value.GetRawText());
            Assert.False(agent.HasExited);
            output.WriteLine($"Actual SDK host pid={pid}, process agent pid={agent.Id}; PostgreSQL attempt snapshots and HTTP reads verified before/after run and retry.");
        }
        finally
        {
            if (!agent.HasExited) agent.Kill(entireProcessTree: true);
            await agent.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
        }

        async Task Wait(Func<Task<bool>> predicate)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            while (!await predicate())
            {
                if (agent.HasExited) Assert.Fail($"Actual agent exited: {await stdout} {await stderr}");
                await Task.Delay(25, timeout.Token);
            }
        }
    }

    [Theory]
    [InlineData("oversize")]
    [InlineData("invalid")]
    [InlineData("null")]
    [InlineData("array")]
    [InlineData("duplicates")]
    [InlineData("utf8")]
    [InlineData("postgres-nul")]
    [InlineData("postgres-number")]
    public async Task TaskOutputs_foreign_gateway_producer_fails_attempt_and_ACKs_without_partial_success(string kind)
    {
        await using var host = await OutputHost.Start(database.DataSource);
        using var channel = GrpcChannel.ForAddress(host.Gateway);
        using var call = new AgentGateway.AgentGatewayClient(channel).Connect(cancellationToken: Token);
        var identity = host.Hello(Guid.NewGuid());
        await call.RequestStream.WriteAsync(new AgentMessage { Sequence = 1, Hello = identity }, Token);
        await Ack(1);
        var session = new AgentSession(Guid.Parse(identity.Id), host.Registry.Find(Guid.Parse(identity.Id))!.SessionId);
        var run = await host.Seed(session, "outputs");
        var bytes = kind switch
        {
            "oversize" => Encoding.UTF8.GetBytes("{\"secret\":\"" + new string('x', 65536) + "\"}"),
            "invalid" => Encoding.UTF8.GetBytes("{private-grant-token"),
            "null" => Encoding.UTF8.GetBytes("null"),
            "array" => Encoding.UTF8.GetBytes("[]"),
            "duplicates" => Encoding.UTF8.GetBytes("""{"x":1,"x":2}"""),
            "utf8" => [(byte)'{', (byte)'"', (byte)'x', (byte)'"', (byte)':', (byte)'"', 0xff, (byte)'"', (byte)'}'],
            "postgres-nul" => Encoding.UTF8.GetBytes("""{"x":"\u0000"}"""),
            _ => Encoding.UTF8.GetBytes("""{"x":1e9999999}""")
        };
        var result = new TaskCompleted
        {
            Task = host.Reference(run.Snapshot.RunId, "outputs"), At = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Outcome = TaskOutcome.Succeeded, OutputsJson = ByteString.CopyFrom(bytes)
        };
        await call.RequestStream.WriteAsync(new AgentMessage { Sequence = 2, TaskCompleted = result }, Token);
        await Ack(2);
        Assert.Equal(ExecutionStatus.Failed, (await host.Runs.LoadAsync(run.Snapshot.RunId, Token)).Snapshot.Tasks["work/outputs"]);
        var error = await host.Scalar(run.Snapshot.RunId, "SELECT error FROM task_attempts WHERE run_id=$1");
        Assert.Contains(kind == "oversize" ? "65536" : "JSON", error);
        Assert.DoesNotContain("private-grant", error);
        Assert.Null((await host.Read(run.Snapshot.RunId, "outputs")).Outputs);
        Assert.Null(await host.Scalar(run.Snapshot.RunId, "SELECT outputs::text FROM node_runs WHERE run_id=$1"));
        // Poison replay must not replace an already committed failure or its null outputs.
        result.OutputsJson = ByteString.CopyFromUtf8("""{"poison":true}""");
        await call.RequestStream.WriteAsync(new AgentMessage { Sequence = 3, TaskCompleted = result }, Token);
        await Ack(3);
        Assert.Null((await host.Read(run.Snapshot.RunId, "outputs")).Outputs);
        await call.RequestStream.CompleteAsync();

        async Task Ack(ulong expected)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            while (await call.ResponseStream.MoveNext(timeout.Token))
                if (call.ResponseStream.Current.Ack?.Sequence >= expected) return;
            Assert.Fail("Gateway did not durably acknowledge the output result.");
        }
    }

    [Fact]
    public async Task TaskOutputs_exact_UTF8_boundary_null_empty_fresh_exact_run_type_Viewer_and_query_contract()
    {
        await using var host = await OutputHost.Start(database.DataSource);
        var session = await host.Registry.RegisterAsync(host.Hello(Guid.NewGuid()), Token);
        var run = await host.Seed(session, "outputs");
        var id = run.Snapshot.RunId;
        Assert.Null((await host.Read(id, "outputs")).Outputs);
        await host.Runs.AcceptAsync(session, new AgentMessage { TaskStarted = new()
        {
            Task = host.Reference(id, "outputs"), At = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow)
        } }, Token);
        var json = "{\"v\":\"" + new string('é', 32764) + "\"}";
        Assert.Equal(65536, Encoding.UTF8.GetByteCount(json));
        await host.Runs.AcceptAsync(session, new AgentMessage { TaskCompleted = new()
        {
            Task = host.Reference(id, "outputs"), At = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Outcome = TaskOutcome.Succeeded, OutputsJson = ByteString.CopyFromUtf8(json)
        } }, Token);
        Assert.Equal(new string('é', 32764), (await host.Read(id, "outputs")).Outputs!.Value.GetProperty("v").GetString());
        Assert.Equal(json, await host.Scalar(id, "SELECT outputs::text FROM task_attempts WHERE run_id=$1"));
        foreach (var suffix in new[]
        {
            "?taskKey=work%2Foutputs&attempt=0", "?taskKey=work%2Foutputs&attempt=32768",
            "?taskKey=work%2Foutputs&attempt=1&attempt=2", "?taskKey=work%2Foutputs&attempt=1&sourceRunId=" + id,
            "?taskKey=work%2Foutputs&attempt=1&grant=private", "?taskKey=work%2Foutputs", "?taskKey=work&attempt=1"
        })
        {
            using var invalid = await host.Client.GetAsync($"/api/v1/runs/{id:D}/outputs" + suffix, Token);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        using (var missing = await host.Client.GetAsync(host.Url(id, "outputs", 2), Token))
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using (var wrongTask = await host.Client.GetAsync(host.Url(id, "elsewhere"), Token))
            Assert.Equal(HttpStatusCode.NotFound, wrongTask.StatusCode);
        using (var unknown = await host.Client.GetAsync(host.Url(Guid.CreateVersion7(), "outputs"), Token))
            Assert.Equal(HttpStatusCode.Forbidden, unknown.StatusCode);
        using (var upper = await host.Client.GetAsync(host.Url(id, "outputs").Replace(id.ToString("D"), id.ToString("D").ToUpperInvariant()), Token))
            Assert.Equal(HttpStatusCode.BadRequest, upper.StatusCode);
        var other = await host.Seed(session, "outputs", type: "other");
        using (var exactType = await host.Client.GetAsync(host.Url(other.Snapshot.RunId, "outputs"), Token))
            Assert.Equal(HttpStatusCode.Forbidden, exactType.StatusCode);
        await host.Execute("DELETE FROM role_bindings WHERE principal_id=$1", host.Viewer);
        using (var revoked = await host.Client.GetAsync(host.Url(id, "outputs"), Token))
            Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
        host.Client.DefaultRequestHeaders.Remove("X-Viewer");
        using (var unauthenticated = await host.Client.GetAsync(host.Url(id, "outputs"), Token))
            Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
    }

    internal static async Task<byte[]> Package()
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var path in Directory.EnumerateFiles(AppContext.BaseDirectory).Where(path => Path.GetExtension(path) is ".dll" or ".json"))
            {
                await using var source = File.OpenRead(path);
                await using var destination = zip.CreateEntry(Path.GetFileName(path), CompressionLevel.Fastest).Open();
                await source.CopyToAsync(destination, Token);
            }
            await using var manifest = new StreamWriter(zip.CreateEntry(PackageCache.ManifestName).Open(), Encoding.UTF8);
            await manifest.WriteAsync($$"""{"version":"1.0.0","entryAssembly":"{{Path.GetFileName(typeof(FixtureMarker).Assembly.Location)}}"}""");
        }
        return buffer.ToArray();
    }

    [Fact]
    public async Task TaskOutputs_database_constraint_rejects_out_of_band_nonobjects_and_UTF8_overflow()
    {
        await using var host = await OutputHost.Start(database.DataSource);
        var session = await host.Registry.RegisterAsync(host.Hello(Guid.NewGuid()), Token);
        var run = await host.Seed(session, "outputs");
        foreach (var json in new[] { "[]", "null", "{\"v\":\"" + new string('é', 32765) + "\"}" })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => host.Execute(
                "UPDATE task_attempts SET outputs=$2::json WHERE run_id=$1", run.Snapshot.RunId, json));
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
            Assert.Null((await host.Read(run.Snapshot.RunId, "outputs")).Outputs);
        }
        await host.Execute("UPDATE task_attempts SET outputs='{}'::json WHERE run_id=$1", run.Snapshot.RunId);
        Assert.Equal("{}", (await host.Read(run.Snapshot.RunId, "outputs")).Outputs!.Value.GetRawText());
    }

    internal static Process StartAgent(OutputHost host, Guid id, Action<ProcessStartInfo>? configure = null)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = host.Root
        };
        foreach (var arg in new[] { "exec", "--runtimeconfig", Path.ChangeExtension(typeof(TaskOutputsTests).Assembly.Location, ".runtimeconfig.json"),
                     "--depsfile", Path.ChangeExtension(typeof(TaskOutputsTests).Assembly.Location, ".deps.json"), typeof(AgentConnection).Assembly.Location })
            start.ArgumentList.Add(arg);
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("Agent__", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        start.Environment["Agent__Id"] = id.ToString("D");
        start.Environment["Agent__Name"] = "outputs-" + id.ToString("N");
        start.Environment["Agent__Pool"] = host.Pool;
        start.Environment["Agent__MaxTasks"] = "4";
        start.Environment["Agent__Engines__0"] = host.Gateway.ToString();
        start.Environment["Agent__ResultJournalDirectory"] = Path.Combine(host.Root, "journal");
        start.Environment["Agent__PackageCache"] = Path.Combine(host.Root, "packages");
        start.Environment["Agent__Sampling__Enabled"] = "false";
        start.Environment["Agent__LogSpoolDirectory"] = Path.Combine(host.Root, "logs");
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("Secrets__", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        start.Environment["Secrets__Provider"] = "Local";
        start.Environment["Secrets__LocalDirectory"] = Path.Combine(host.Root, "secrets");
        start.Environment["TEMP"] = host.ProjectRoot;
        start.Environment["TMP"] = host.ProjectRoot;
        start.Environment["TMPDIR"] = host.ProjectRoot;
        configure?.Invoke(start);
        return Process.Start(start)!;
    }

    internal sealed class OutputHost : IAsyncDisposable
    {
        public required WebApplication App { get; init; }
        public required HttpClient Client { get; init; }
        public required Uri Gateway { get; init; }
        public required string Root { get; init; }
        public required string ProjectRoot { get; init; }
        public required RunStore Runs { get; init; }
        public required AgentRegistry Registry { get; init; }
        public required FileSystemBlobStore Blobs { get; init; }
        private NpgsqlDataSource Source { get; init; } = null!;
        public Guid Viewer { get; } = Guid.NewGuid();
        public string Pool { get; } = "outputs-" + Guid.NewGuid().ToString("N");
        private readonly string team = "outputs-" + Guid.NewGuid().ToString("N");
        private bool seeded;

        public static async Task<OutputHost> Start(NpgsqlDataSource source, string artifacts = "task-outputs-3adec91f",
            Action<WebApplicationBuilder>? configure = null, bool uploadLogs = false)
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (!Directory.Exists(Path.Combine(directory.FullName, ".git")))
                directory = directory.Parent ?? throw new InvalidOperationException("Repository root not found.");
            var root = Path.Combine(directory.FullName, ".agent-artifacts", artifacts, "tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var runs = new RunStore(source, NullLogger<RunStore>.Instance);
            var registry = new AgentRegistry(source, TimeProvider.System);
            var blobs = new FileSystemBlobStore(Path.Combine(root, "blobs"), RandomNumberGenerator.GetBytes(32));
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http1);
                options.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http2);
            });
            builder.Services.AddGrpc();
            builder.Services.AddAuthentication("fixture").AddScheme<AuthenticationSchemeOptions, ViewerAuthentication>("fixture", _ => { });
            builder.Services.AddTeamAuthorization();
            builder.Services.AddSingleton(source);
            builder.Services.AddSingleton(registry);
            builder.Services.AddSingleton(runs);
            builder.Services.AddSingleton(new ResourceSampleSink(source, runs, blobs));
            if (uploadLogs)
            {
                var logKey = RandomNumberGenerator.GetBytes(32);
                builder.Services.AddSingleton(TimeProvider.System);
                builder.Services.AddSingleton(new LogUploadCapabilities(logKey, TimeProvider.System));
                builder.Services.AddSingleton(new BlobUploadGrants(logKey));
                builder.Services.AddSingleton<IBlobStore>(blobs);
            }
            var documentName = "v1";
            builder.Services.AddOpenApi(documentName);
            configure?.Invoke(builder);
            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapGrpcService<AgentGatewayService>();
            app.MapTaskOutputsEndpoints();
            if (uploadLogs) app.MapLogUploadEndpoints();
            app.MapOpenApi();
            await app.StartAsync(Token);
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.ToArray();
            var host = new OutputHost
            {
                App = app, Root = root, ProjectRoot = directory.FullName, Runs = runs, Registry = registry, Blobs = blobs, Source = source,
                Client = new() { BaseAddress = new(addresses[0]) }, Gateway = new(addresses[1])
            };
            host.Client.DefaultRequestHeaders.Add("X-Viewer", host.Viewer.ToString("D"));
            return host;
        }

        public Hello Hello(Guid id) => new()
        {
            Id = id.ToString("D"), Name = "outputs-" + id.ToString("N"), Os = OperatingSystem.IsWindows() ? "windows" : "linux",
            Arch = "x64", Cores = 2, RamMb = 8192, MaxTasks = 4, Pool = Pool, Version = "1", ProtocolVersion = 1
        };
        public AttemptRef Reference(Guid run, string task, uint attempt = 1) =>
            new() { RunId = run.ToString("D"), NodeKey = "work", TaskKey = task, Attempt = attempt };

        public async Task<RunState> Seed(AgentSession session, string task, byte[]? package = null, string type = "test",
            ByteString? variablesJson = null)
        {
            if (!seeded)
            {
                await Execute("INSERT INTO teams(id,name) VALUES($1,$1)", team);
                foreach (var name in new[] { "test", "other" })
                    await Execute("""
                        INSERT INTO workflow_types(id,team_id,name,normalized_name,description,metadata_schema)
                        VALUES($1||'/'||$2,$1,$2,$2,'outputs','{}')
                        """, team, name);
                await Execute("""
                    INSERT INTO role_bindings(id,scope_kind,scope_id,role,principal_id,principal_kind) VALUES($1,2,$2,0,$3,1)
                    """, Guid.CreateVersion7(), team + "/test", Viewer);
                seeded = true;
            }
            using var variables = variablesJson is null ? null : JsonDocument.Parse(variablesJson.Memory);
            var run = new RunState(Guid.CreateVersion7(), [new("work", [], [new(task)])],
                new(type, ImmutableSortedDictionary<string, string>.Empty), Assert.Fail,
                variables?.RootElement.EnumerateObject().ToDictionary(property => property.Name,
                    property => property.Value, StringComparer.Ordinal));
            await Runs.CreateAsync(run, $"{team}/{type}/workflow", Guid.CreateVersion7(), DateTimeOffset.UtcNow.AddMinutes(5), Token);
            await Runs.AppendAsync(run, EventEntity.Run, null, (int)RunStatus.Running, DateTimeOffset.UtcNow, Token);
            await Runs.AppendAsync(run, EventEntity.Node, "work", (int)ExecutionStatus.Ready, DateTimeOffset.UtcNow, Token);
            await Runs.AppendAsync(run, EventEntity.Node, "work", (int)ExecutionStatus.Dispatched, DateTimeOffset.UtcNow, Token);
            await Runs.AppendAsync(run, EventEntity.Node, "work", (int)ExecutionStatus.Running, DateTimeOffset.UtcNow, Token);
            await Runs.AppendAsync(run, EventEntity.Task, "work/" + task, (int)ExecutionStatus.Ready, DateTimeOffset.UtcNow, Token);
            await Dispatch(run, session, task, 1, package, variablesJson);
            return run;
        }

        public async Task Dispatch(RunState run, AgentSession session, string task, int attempt, byte[]? package,
            ByteString? variablesJson = null)
        {
            var sha = package is null ? new string('a', 64) : Convert.ToHexStringLower(SHA256.HashData(package));
            using var dispatcher = new Dispatcher(Source, Runs, Registry, new PlacementEngine(), TimeProvider.System);
            var decision = await dispatcher.DispatchAsync(run,
                new(run.Snapshot.RunId, "work", task, attempt, sha, new(new(pool: Pool))), new()
                {
                    Task = Reference(run.Snapshot.RunId, task, (uint)attempt), Version = "1.0.0",
                    PackageUri = new Uri(Client.BaseAddress!, "/package.zip").ToString(), Sha256 = sha,
                    VariablesJson = variablesJson ?? ByteString.CopyFromUtf8($$"""{"amount":{{attempt}}}"""),
                    DeadlineAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(2))
                }, Token);
            Assert.Equal(session.AgentId, decision.Agent?.Id);
        }

        public string Url(Guid run, string task, int attempt = 1) =>
            $"/api/v1/runs/{run:D}/outputs?taskKey=work%2F{Uri.EscapeDataString(task)}&attempt={attempt}";
        public async Task<TaskOutputsResponse> Read(Guid run, string task, int attempt = 1)
        {
            using var response = await Client.GetAsync(Url(run, task, attempt), Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
            var text = await response.Content.ReadAsStringAsync(Token);
            using var shape = JsonDocument.Parse(text);
            Assert.True(shape.RootElement.TryGetProperty("outputs", out _));
            Assert.True(shape.RootElement.TryGetProperty("runId", out _));
            Assert.DoesNotContain("uploadUri", text);
            Assert.DoesNotContain("error", text);
            return JsonSerializer.Deserialize(text, TaskOutputsJsonContext.Default.TaskOutputsResponse)!;
        }
        public async Task<string?> Scalar(Guid run, string sql)
        {
            await using var command = Source.CreateCommand(sql);
            command.Parameters.AddWithValue(run);
            return await command.ExecuteScalarAsync(Token) as string;
        }
        public async Task Execute(string sql, params object[] values)
        {
            await using var command = Source.CreateCommand(sql);
            foreach (var value in values) command.Parameters.AddWithValue(value);
            await command.ExecuteNonQueryAsync(Token);
        }
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.StopAsync(CancellationToken.None);
            await App.DisposeAsync();
            Registry.Dispose();
            Blobs.Dispose();
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class ViewerAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(Guid.TryParse(Request.Headers["X-Viewer"], out var viewer)
                ? AuthenticateResult.Success(new(new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim("oid", viewer.ToString("D")), new Claim("idtyp", "user")], Scheme.Name)), Scheme.Name))
                : AuthenticateResult.NoResult());
    }
}

public sealed class TaskOutputsDatabase : AgentDatabase
{
    protected override IReadOnlyList<string> Migrations => Migrator.LoadScripts().Select(script => script.Name).ToArray();
}
