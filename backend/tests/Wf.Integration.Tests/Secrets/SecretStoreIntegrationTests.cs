using System.Diagnostics;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Wf.Agent;
using Wf.Agent.Secrets;
using Wf.Contracts;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Definition;
using Wf.ControlPlane.Engine.Agents;
using Wf.Engine.Core;
using Wf.Integration.Tests.Outputs;

namespace Wf.Integration.Tests.Secrets;

public sealed class SecretStoreIntegrationTests(TaskOutputsDatabase database, ITestOutputHelper output)
    : IClassFixture<TaskOutputsDatabase>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Reference = "secret://local/integration-credential";
    private readonly string value = "runtime-only-clé-🔑-" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task Protected_local_values_actual_agent_SDK_gateway_Postgres_errors_outputs_and_log_spool_are_safe()
    {
        var completions = new ConcurrentQueue<TaskCompleted>();
        var packageGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await TaskOutputsTests.OutputHost.Start(database.DataSource, "secret-stores-3adec91f", builder =>
        {
            builder.Services.AddSingleton(new CompletionObserver(completions.Enqueue, packageGate.Task));
            builder.Services.AddGrpc(options => options.Interceptors.Add<CompletionObserver>());
        });
        var package = await TaskOutputsTests.Package();
        host.App.MapGet("/package.zip", async (HttpContext context) =>
        {
            await packageGate.Task.WaitAsync(context.RequestAborted);
            return Results.Bytes(package, "application/zip");
        });
        var secretDirectory = Path.Combine(host.Root, "secrets");
        var nativeAgent = Environment.GetEnvironmentVariable("WF_SECRET_NATIVE_AGENT");
        await Provision(secretDirectory, value, nativeAgent);
        var local = new LocalSecretStore(secretDirectory);
        Assert.Equal(value, await local.ResolveAsync(new(Reference), Token));
        var id = Guid.NewGuid();
        using var agent = TaskOutputsTests.StartAgent(host, id, start =>
        {
            if (nativeAgent is null) return;
            start.FileName = nativeAgent;
            start.ArgumentList.Clear();
        });
        var stdout = agent.StandardOutput.ReadToEndAsync(Token);
        var stderr = agent.StandardError.ReadToEndAsync(Token);
        var runIds = new List<Guid>();
        try
        {
            await Wait(() => Task.FromResult(host.Registry.Find(id) is not null));
            var session = new AgentSession(id, host.Registry.Find(id)!.SessionId);
            var variables = ByteString.CopyFromUtf8(
                System.Text.Json.JsonSerializer.Serialize(new { credential = new { Name = Reference } }));
            foreach (var task in new[] { "secret", "secretFailure" })
            {
                var run = await host.Seed(session, task, package, variablesJson: variables);
                runIds.Add(run.Snapshot.RunId);
                if (task == "secret")
                {
                    // Hold the real package download until inspecting the durable, references-only dispatch.
                    var pending = await host.Scalar(run.Snapshot.RunId, "SELECT encode(payload,'base64') FROM dispatch_outbox WHERE run_id=$1");
                    Assert.NotNull(pending);
                    var dispatch = Dispatch.Parser.ParseFrom(Convert.FromBase64String(pending));
                    Assert.Contains(Reference, dispatch.VariablesJson.ToStringUtf8());
                    AssertSafe(dispatch.ToString());
                    packageGate.TrySetResult();
                }
                var expected = task == "secret" ? ExecutionStatus.Succeeded : ExecutionStatus.Failed;
                await Wait(async () => (await host.Runs.LoadAsync(run.Snapshot.RunId, Token)).Snapshot.Tasks["work/" + task] == expected);
                var result = await host.Read(run.Snapshot.RunId, task);
                Assert.Equal("***", result.Outputs!.Value.GetProperty("value").GetString());
                Assert.Equal("***", result.Outputs.Value.GetProperty("encoded").GetString());
                Assert.True(result.Outputs.Value.GetProperty("launchClean").GetBoolean());
                Assert.Equal(Reference, result.Outputs.Value.GetProperty("reference").GetString());
                Assert.Contains(Reference, await host.Scalar(run.Snapshot.RunId, "SELECT variables::text FROM runs WHERE id=$1"));
                var pid = result.Outputs.Value.GetProperty("pid").GetInt32();
                Assert.NotEqual(Environment.ProcessId, pid);
                Assert.NotEqual(agent.Id, pid);
                if (task == "secretFailure")
                    Assert.Contains("***", await host.Scalar(run.Snapshot.RunId, "SELECT error FROM task_attempts WHERE run_id=$1"));
                output.WriteLine($"Real SDK pid={pid}; agent pid={agent.Id}; {task} {expected}; outputs and errors masked.");
            }
            var missing = await host.Seed(session, "secret", package, variablesJson:
                ByteString.CopyFromUtf8("""{"credential":{"Name":"secret://local/missing"}}"""));
            runIds.Add(missing.Snapshot.RunId);
            await Wait(async () => (await host.Runs.LoadAsync(missing.Snapshot.RunId, Token)).Snapshot.Tasks["work/secret"] == ExecutionStatus.Failed);
            var attemptError = await host.Scalar(missing.Snapshot.RunId, "SELECT error FROM task_attempts WHERE run_id=$1");
            Assert.Contains("secret://local/missing", attemptError);
            var snapshot = await host.Runs.LoadAsync(missing.Snapshot.RunId, Token);
            Assert.Null((await host.Read(missing.Snapshot.RunId, "secret")).Outputs);
            Assert.Equal(FailureCategory.ValidationError, snapshot.Snapshot.Metrics!.Tasks["work/secret"].Attempts.Single().FailureCategory);
            var wireFailure = Assert.Single(completions, completion => completion.Task.RunId == missing.Snapshot.RunId.ToString("D"));
            Assert.Equal("ValidationError", wireFailure.Error.Category);
            Assert.True(wireFailure.Error.NonRetryable);

            foreach (var run in runIds)
            {
                var db = await host.Scalar(run, """
                    SELECT json_build_object('run',row_to_json(r),
                      'events',(SELECT json_agg(row_to_json(e)) FROM run_events e WHERE e.run_id=r.id),
                      'attempts',(SELECT json_agg(row_to_json(a)) FROM task_attempts a WHERE a.run_id=r.id),
                      'snapshots',(SELECT json_agg(convert_from(s.payload,'UTF8')) FROM run_snapshots s WHERE s.run_id=r.id),
                      'dispatch',(SELECT json_agg(encode(d.payload,'base64')) FROM dispatch_outbox d WHERE d.run_id=r.id))::text
                    FROM runs r WHERE r.id=$1
                    """);
                AssertSafe(db!);
            }
            var logRoot = Path.Combine(host.Root, "logs");
            await Wait(() => Task.FromResult(Directory.Exists(logRoot) &&
                Directory.GetFiles(logRoot, "index.json", SearchOption.AllDirectories).Length == 3));
            await Wait(() => Task.FromResult(Directory.GetFiles(logRoot, "*.log.gz", SearchOption.AllDirectories).Length >= 2));
            var logs = new StringBuilder();
            foreach (var path in Directory.GetFiles(logRoot, "*.log.gz", SearchOption.AllDirectories))
            {
                await using var file = File.OpenRead(path);
                await using var gzip = new GZipStream(file, CompressionMode.Decompress);
                using var reader = new StreamReader(gzip, Encoding.UTF8);
                logs.Append(await reader.ReadToEndAsync(Token));
            }
            AssertSafe(logs.ToString());
            Assert.Contains("ctx *** ***", logs.ToString());
            Assert.Contains("out *** tail", logs.ToString());
            Assert.False(agent.HasExited);
        }
        finally
        {
            packageGate.TrySetResult();
            if (!agent.HasExited) agent.Kill(entireProcessTree: true);
            await agent.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
        }
        AssertSafe(await stdout + await stderr);
        foreach (var file in Directory.GetFiles(Path.Combine(host.Root, "journal"), "*", SearchOption.AllDirectories))
            AssertSafe(Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file, Token)));
        output.WriteLine("Actual protected provisioning + runtime resolution, safe PostgreSQL/events/journal/gzip spool and missing-secret ValidationError verified. No live Azure tenant call.");

        async Task Wait(Func<Task<bool>> predicate)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            while (!await predicate())
            {
                if (agent.HasExited) Assert.Fail($"Actual agent exited with code {agent.ExitCode}.");
                await Task.Delay(25, timeout.Token);
            }
        }
    }

    private static async Task Provision(string directory, string value, string? nativeAgent)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false)
        };
        foreach (var argument in new[] { "exec", "--runtimeconfig",
            Path.ChangeExtension(typeof(SecretStoreIntegrationTests).Assembly.Location, ".runtimeconfig.json"),
            "--depsfile", Path.ChangeExtension(typeof(SecretStoreIntegrationTests).Assembly.Location, ".deps.json"),
            typeof(AgentConnection).Assembly.Location, "--set-secret", Reference })
            start.ArgumentList.Add(argument);
        if (nativeAgent is not null)
        {
            start.FileName = nativeAgent;
            start.ArgumentList.Clear();
            start.ArgumentList.Add("--set-secret");
            start.ArgumentList.Add(Reference);
        }
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("Secrets__", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        start.Environment["Secrets__Provider"] = "Local";
        start.Environment["Secrets__LocalDirectory"] = directory;
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(Token);
        var stderr = process.StandardError.ReadToEndAsync(Token);
        try
        {
            await process.StandardInput.WriteAsync(value.AsMemory(), Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(Token).WaitAsync(TimeSpan.FromSeconds(20), Token);
            Assert.Equal(0, process.ExitCode);
            Assert.Empty(await stdout);
            Assert.Empty(await stderr);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
        }
    }

    private void AssertSafe(string text)
    {
        Assert.DoesNotContain(value, text);
        Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(value)), text);
        Assert.DoesNotContain(System.Text.Json.JsonEncodedText.Encode(value).ToString(), text, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class CompletionObserver(Action<TaskCompleted> observe, Task releaseStarted) : Interceptor
    {
        public override Task DuplexStreamingServerHandler<TRequest, TResponse>(IAsyncStreamReader<TRequest> requestStream,
            IServerStreamWriter<TResponse> responseStream, ServerCallContext context,
            DuplexStreamingServerMethod<TRequest, TResponse> continuation) =>
            continuation(new ObservingReader<TRequest>(requestStream, async message =>
            {
                if (message is AgentMessage { TaskStarted: not null })
                    await releaseStarted.WaitAsync(context.CancellationToken);
                if (message is AgentMessage { TaskCompleted: { } completion }) observe(completion.Clone());
            }), responseStream, context);
    }

    private sealed class ObservingReader<T>(IAsyncStreamReader<T> inner, Func<T, Task> observe) : IAsyncStreamReader<T>
    {
        public T Current => inner.Current;
        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            if (!await inner.MoveNext(cancellationToken)) return false;
            await observe(inner.Current);
            return true;
        }
    }
}
