using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Wf.Agent;
using Wf.Cli.Packaging;
using Wf.Contracts;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Events;
using Wf.Contracts.Manifest;
using Wf.Contracts.Registration;
using Wf.Contracts.Registry;
using Wf.ControlPlane.Engine;
using Wf.ControlPlane.Engine.Agents;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Registry;
using Wf.ControlPlane.Storage;
using Wf.Engine.Core;
using Wf.Engine.Core.Placement;
using Wf.Engine.Core.Stages;
using Wf.Integration.Tests.Registration;
using Wf.Validation.Build;
using RegistrationServer = Wf.Integration.Tests.Registration.RegistrationTests.Server;

namespace Wf.Integration.Tests;

public sealed class HotActivationTests(RegistrationDatabase database, ITestOutputHelper output)
    : IClassFixture<RegistrationDatabase>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task HotActivation_twenty_executing_old_packages_finish_pinned_while_next_run_executes_new_version_without_restart()
    {
        var application = "hot-cache-" + Guid.NewGuid().ToString("N");
        await using var source = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(database.ConnectionString)
            { ApplicationName = application }.ConnectionString);
        await using var registration = await RegistrationServer.Start(source, runs: true);
        var root = Path.Combine(registration.Root, "hot");
        Directory.CreateDirectory(root);
        using var registry = new AgentRegistry(database.DataSource, TimeProvider.System);
        var store = new RunStore(database.DataSource, NullLogger<RunStore>.Instance);
        using var dispatcher = new Dispatcher(database.DataSource, store, registry, new PlacementEngine(), TimeProvider.System);
        var entered = new ConcurrentDictionary<int, string>();
        var downloads = new ConcurrentDictionary<string, int>();
        var packages = new ConcurrentDictionary<string, BlobContent>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http1);
            options.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http2);
        });
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(registry);
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton(new ResourceSampleSink(database.DataSource, store, registration.Blobs));
        builder.Services.AddSingleton(registration.Blobs);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(new LogUploadCapabilities(RandomNumberGenerator.GetBytes(32), TimeProvider.System));
        builder.Services.AddSingleton(new BlobUploadGrants(RandomNumberGenerator.GetBytes(32)));
        await using var gateway = builder.Build();
        gateway.MapGrpcService<AgentGatewayService>();
        gateway.MapLogUploadEndpoints();
        gateway.MapGet("/package/{sha}", async (string sha, HttpContext context) =>
        {
            downloads.AddOrUpdate(sha, 1, (_, count) => count + 1);
            using var buffer = new MemoryStream();
            await registration.Blobs.DownloadAsync(packages[sha], buffer, context.RequestAborted);
            return Results.Bytes(buffer.ToArray(), "application/zip");
        });
        gateway.MapGet("/barrier/{version}/{pid:int}", async (string version, int pid, HttpContext context) =>
        {
            Assert.True(entered.TryAdd(pid, version), "Each held attempt must execute in its own live task-host process.");
            if (version == "1.0.0") await release.Task.WaitAsync(context.RequestAborted);
            return Results.Ok();
        });
        gateway.MapGet("/healthz", () => Results.Ok());
        await gateway.StartAsync(Token);
        var addresses = gateway.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.ToArray();
        var origin = new Uri(addresses[0]);
        using var health = new HttpClient { BaseAddress = origin };
        using (var ready = await health.GetAsync("/healthz", Token)) Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        var workflow = "hot-" + Guid.NewGuid().ToString("N") + "/release/workflow";
        var before = await Build("1.0.0");
        var after = await Build("1.1.0");
        Assert.NotEqual(before.Package.Sha256, after.Package.Sha256);
        var old = await Register(before);
        await Wait(() => Task.FromResult(HasVersion(registration.Versions, workflow, old)), TimeSpan.FromSeconds(5));
        var pinned = registration.Versions.GetActive(workflow);
        var listener = await Listener();
        var enginePid = Environment.ProcessId;
        using var engineProcess = Process.GetCurrentProcess();
        var engineStarted = engineProcess.StartTime;
        var cacheExecution = registration.Versions.ExecuteTask;
        Assert.NotNull(cacheExecution);
        var agentId = Guid.NewGuid();
        var pool = "hot-" + agentId.ToString("N");
        using var agent = StartAgent(new Uri(addresses[1]), agentId, pool, root);
        var agentStarted = agent.StartTime;
        var stdout = agent.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = agent.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await Wait(() => Task.FromResult(registry.Find(agentId) is not null), TimeSpan.FromSeconds(30), agent);
            var session = registry.Find(agentId)!.SessionId;
            var runs = new List<Guid>();
            for (var index = 0; index < 20; index++) runs.Add(await Dispatch());
            await Wait(async () => entered.Count == 20 &&
                await Value("SELECT count(*) FROM task_attempts WHERE run_id=ANY($1) AND status=$2",
                    runs.ToArray(), (short)ExecutionStatus.Running) is 20L, TimeSpan.FromSeconds(90), agent);
            Assert.Equal(20, entered.Count);
            Assert.All(entered.Values, version => Assert.Equal("1.0.0", version));
            Assert.Equal(20L, await Value("SELECT count(*) FROM runs WHERE id=ANY($1) AND status=$2 AND version_id=$3",
                runs.ToArray(), (short)RunStatus.Running, old));
            var oldProcesses = entered.Keys.Select(Process.GetProcessById).ToArray();
            try
            {
                Assert.All(oldProcesses, process => Assert.False(process.HasExited));
                var elapsed = Stopwatch.StartNew();
                var next = await Register(after);
                await Wait(() => Task.FromResult(registration.Versions.GetActive(workflow).VersionId == next),
                    TimeSpan.FromSeconds(5), agent);
                var activation = elapsed.Elapsed;
                Assert.True(activation <= TimeSpan.FromSeconds(5), $"Activation took {activation}.");
                Assert.NotEqual(old, next);
                Assert.Equal(old, pinned.VersionId);
                Assert.Equal("1.0.0", pinned.Manifest.Version);
                Assert.Equal(before.Package.Sha256, pinned.Package.Sha256);
                Assert.False(release.Task.IsCompleted);
                Assert.All(oldProcesses, process => Assert.False(process.HasExited));
                await AssertContinuity();

                var newRun = await Dispatch();
                await Finish(newRun, next, "1.1.0");
                Assert.False(release.Task.IsCompleted);
                Assert.Equal(20L, await Value("SELECT count(*) FROM task_attempts WHERE run_id=ANY($1) AND status=$2",
                    runs.ToArray(), (short)ExecutionStatus.Running));
                Assert.All(oldProcesses, process => Assert.False(process.HasExited));
                release.SetResult();
                foreach (var run in runs) await Finish(run, old, "1.0.0");
                Assert.Equal(20L, await Value("SELECT count(*) FROM runs WHERE id=ANY($1) AND status=$2 AND version_id=$3",
                    runs.ToArray(), (short)RunStatus.Succeeded, old));
                Assert.Equal(21, entered.Count);
                Assert.Equal(20, entered.Values.Count(version => version == "1.0.0"));
                Assert.Equal(1, entered.Values.Count(version => version == "1.1.0"));
                Assert.Equal(1, downloads[before.Package.Sha256]);
                Assert.Equal(1, downloads[after.Package.Sha256]);
                Assert.Equal(2, downloads.Count);
                Assert.Equal(2L, await Value("SELECT count(*) FROM workflow_versions WHERE workflow_id=$1", workflow));
                await AssertContinuity();
                Assert.Empty(registration.Failures);
                output.WriteLine($"20 actual SDK task hosts held across activation; old UUID={old:D}; next UUID={next:D}; " +
                    $"activation={activation.TotalMilliseconds:F3} ms; 20 old runs + 1 next run succeeded; " +
                    $"engine PID={enginePid}; agent PID={agent.Id}; agent session={session:D}; listener PID={listener}; " +
                    $"package downloads={string.Join(',', downloads.Values)}; no restarts/reconnects.");

                async Task AssertContinuity()
                {
                    Assert.Equal(enginePid, Environment.ProcessId);
                    using var engine = Process.GetCurrentProcess();
                    Assert.Equal(engineStarted, engine.StartTime);
                    Assert.Same(cacheExecution, registration.Versions.ExecuteTask);
                    Assert.False(cacheExecution.IsCompleted);
                    Assert.Equal(listener, await Listener());
                    Assert.False(agent.HasExited);
                    Assert.Equal(agentStarted, agent.StartTime);
                    Assert.Equal(session, registry.Find(agentId)!.SessionId);
                    Assert.Equal(session, await Value("SELECT session_id FROM agents WHERE id=$1", agentId));
                }
            }
            finally { foreach (var process in oldProcesses) process.Dispose(); }
        }
        catch
        {
            output.WriteLine("Agent still running: " + !agent.HasExited);
            throw;
        }
        finally
        {
            release.TrySetResult();
            if (!agent.HasExited) agent.Kill(entireProcessTree: true);
            await agent.WaitForExitAsync(CancellationToken.None);
            output.WriteLine(await stdout);
            output.WriteLine(await stderr);
            await gateway.StopAsync(CancellationToken.None);
        }

        async Task<Guid> Dispatch()
        {
            var selected = registration.Versions.GetActive(workflow);
            var manifest = selected.Manifest;
            var run = new RunState(Guid.CreateVersion7(), manifest.Nodes, manifest.Metadata, Assert.Fail);
            await store.CreateAsync(run, workflow, selected.VersionId, DateTimeOffset.UtcNow.AddMinutes(3), Token);
            await store.AppendAsync(run, EventEntity.Run, null, (int)RunStatus.Running, DateTimeOffset.UtcNow, Token);
            foreach (var state in new[] { ExecutionStatus.Ready, ExecutionStatus.Dispatched, ExecutionStatus.Running })
                await store.AppendAsync(run, EventEntity.Node, "work", (int)state, DateTimeOffset.UtcNow, Token);
            await store.AppendAsync(run, EventEntity.Task, "work/task", (int)ExecutionStatus.Ready, DateTimeOffset.UtcNow, Token);
            var decision = await dispatcher.DispatchAsync(run,
                new(run.Snapshot.RunId, "work", "task", 1, selected.Package.Sha256, new(new(pool: pool))), new()
                {
                    Task = new() { RunId = run.Snapshot.RunId.ToString("D"), NodeKey = "work", TaskKey = "task", Attempt = 1 },
                    Version = manifest.Version, Sha256 = selected.Package.Sha256,
                    PackageUri = new Uri(origin, "/package/" + selected.Package.Sha256).ToString(),
                    DeadlineAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(3))
                }, Token);
            Assert.Equal(agentId, decision.Agent?.Id);
            return run.Snapshot.RunId;
        }

        async Task Finish(Guid id, Guid version, string semver)
        {
            await Wait(async () => (await store.LoadAsync(id, Token)).Snapshot.Tasks["work/task"] == ExecutionStatus.Succeeded,
                TimeSpan.FromSeconds(90), agent);
            Assert.Equal(version, await Value("SELECT version_id FROM runs WHERE id=$1", id));
            var text = Assert.IsType<string>(await Value("SELECT outputs::text FROM node_runs WHERE run_id=$1 AND node_key='work'", id));
            using var json = JsonDocument.Parse(text);
            Assert.Equal(semver, json.RootElement.GetProperty("version").GetString());
            var pid = json.RootElement.GetProperty("pid").GetInt32();
            Assert.Equal(semver, entered[pid]);
            Assert.NotEqual(Environment.ProcessId, pid);
            Assert.NotEqual(agent.Id, pid);
            // Drive durable lifecycle transitions only after the real agent's committed success and versioned output.
            var run = await store.LoadAsync(id, Token);
            await store.AppendAsync(run, EventEntity.Node, "work", (int)ExecutionStatus.Succeeded, DateTimeOffset.UtcNow, Token);
            Assert.True(StagePlanner.Plan(before.Manifest.Nodes, run.Snapshot.Nodes, run.Snapshot.Status).Complete);
            await store.AppendAsync(run, EventEntity.Run, null, (int)RunStatus.Succeeded, DateTimeOffset.UtcNow, Token);
            Assert.Equal(RunStatus.Succeeded, (await store.LoadAsync(id, Token)).Snapshot.Status);
        }

        async Task<Compiled> Build(string version)
        {
            var directory = Path.Combine(root, version);
            Directory.CreateDirectory(directory);
            var sdk = Path.Combine(Repository, "backend", "src", "Wf.Sdk", "Wf.Sdk.csproj");
            var source = $$"""
                #:project {{sdk}}
                #:property PublishAot=false
                #:property AssemblyName=HotActivation.Workflow
                using Wf.Sdk;
                var workflow = Workflow.Define("{{workflow}}", "{{version}}", w => w.Type("release")
                    .Node("work", n => n.Task("task", async ctx =>
                    {
                        using var client = new System.Net.Http.HttpClient();
                        using var response = await client.GetAsync("{{origin}}barrier/{{version}}/" + Environment.ProcessId,
                            ctx.CancellationToken);
                        response.EnsureSuccessStatusCode();
                        ctx.Outputs["version"] = "{{version}}";
                        ctx.Outputs["pid"] = Environment.ProcessId;
                    })));
                return await workflow.RunAsync(args);
                """;
            var script = Path.Combine(directory, "workflow.cs");
            await File.WriteAllTextAsync(script, source, new UTF8Encoding(false), Token);
            var inputs = new[] { "Wf.Sdk", "Wf.Contracts", "Wf.Engine.Core", "Wf.Validation" }
                .Select(name => Path.Combine(Repository, "backend", "src", name))
                .Append(Path.Combine(Repository, "backend", "proto")).ToArray();
            var cache = new ScriptBuildCache(Path.Combine(root, "cache"), Path.Combine(root, "build"));
            var build = await cache.BuildAsync(new(script, directory, inputs), Token);
            var package = await PackageBuilder.BuildAsync(build, Path.Combine(root, version + ".zip"), Token);
            return new(build.Manifest, source, package);
        }

        async Task<Guid> Register(Compiled compiled)
        {
            var reference = new PackageReference(compiled.Package.Sha256, compiled.Package.Size);
            using var grant = await registration.Grant(reference);
            Assert.Equal(HttpStatusCode.Created, grant.StatusCode);
            var uri = (await RegistrationTests.Json(grant)).GetProperty("uploadUri").GetString()!;
            using var uploaded = await registration.Upload(uri, await File.ReadAllBytesAsync(compiled.Package.Path, Token));
            Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
            packages.TryAdd(reference.Sha256, new(BlobKind.Packages, reference.Sha256, reference.Size));
            var team = workflow.Split('/')[0];
            var teamRegistry = new TeamRegistry(team, [new(team, "release", "Hot activation", new([]))]);
            using var response = await registration.Post(new(new('a', 40), "Hot activation integration", workflow,
                JsonSerializer.SerializeToElement(compiled.Manifest, WorkflowManifestJsonContext.Default.WorkflowManifest),
                reference, compiled.Source, JsonSerializer.SerializeToElement(teamRegistry, TeamRegistryJsonContext.Default.TeamRegistry)));
            Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Token));
            return (await RegistrationTests.Json(response)).GetProperty("workflowVersion").GetGuid();
        }

        async Task<int> Listener() => Assert.IsType<int>(await Value("""
            SELECT pid FROM pg_stat_activity WHERE query IN ('LISTEN registry','SELECT 1') AND application_name=$1
            """, application));
    }

    private async Task<object?> Value(string sql, params object[] parameters)
    {
        await using var command = database.DataSource.CreateCommand(sql);
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter);
        return await command.ExecuteScalarAsync(Token);
    }

    private static bool HasVersion(ActiveVersionCache cache, string workflow, Guid version)
    {
        if (!cache.IsReady) return false;
        try { return cache.GetActive(workflow).VersionId == version; }
        catch (KeyNotFoundException) { return false; }
    }

    private static async Task Wait(Func<Task<bool>> condition, TimeSpan duration, Process? agent = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(duration);
        while (!await condition())
        {
            if (agent is { HasExited: true }) Assert.Fail($"Agent exited with code {agent.ExitCode}.");
            await Task.Delay(25, deadline.Token);
        }
    }

    private static Process StartAgent(Uri gateway, Guid id, string pool, string root)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root
        };
        foreach (var argument in new[] { "exec", "--runtimeconfig", Path.ChangeExtension(typeof(HotActivationTests).Assembly.Location, ".runtimeconfig.json"),
            "--depsfile", Path.ChangeExtension(typeof(HotActivationTests).Assembly.Location, ".deps.json"), typeof(AgentConnection).Assembly.Location })
            start.ArgumentList.Add(argument);
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("Agent__", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        start.Environment["Agent__Id"] = id.ToString("D");
        start.Environment["Agent__Name"] = pool;
        start.Environment["Agent__Pool"] = pool;
        start.Environment["Agent__MaxTasks"] = "21";
        start.Environment["Agent__Engines__0"] = gateway.ToString();
        start.Environment["Agent__ResultJournalDirectory"] = Path.Combine(root, "journal");
        start.Environment["Agent__PackageCache"] = Path.Combine(root, "packages");
        start.Environment["Agent__Sampling__Enabled"] = "false";
        return Process.Start(start)!;
    }

    private static string Repository
    {
        get
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
                if (Directory.Exists(Path.Combine(directory.FullName, "backend", "proto"))) return directory.FullName;
            throw new DirectoryNotFoundException("Hot activation integration requires the repository source tree.");
        }
    }

    private sealed record Compiled(WorkflowManifest Manifest, string Source, PackageBuildResult Package);
}
