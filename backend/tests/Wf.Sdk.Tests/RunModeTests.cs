using System.Net;
using System.Text;
using System.Text.Json;
using Wf.Sdk.Hosting;

namespace Wf.Sdk.Tests;

public sealed class RunModeTests
{
    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(null)]
    [InlineData("worker")]
    public async Task Single_machine_engine_submission_preserves_override_without_local_execution(string? machine)
    {
        using var client = new HttpClient(new Handler(async (request, token) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var placement = body.RootElement.GetProperty("placement");
            Assert.True(placement.GetProperty("singleMachine").GetBoolean());
            Assert.Equal(machine, placement.GetProperty("machine").GetString());
            return Accepted(Guid.CreateVersion7());
        }));
        using var output = new StringWriter();
        string[] arguments = ["--engine", "http://localhost", "--single-machine", "--var", "amount=7"];
        if (machine is not null) arguments = [.. arguments, "--machine", machine];
        Assert.Equal(0, await RunModeDispatcher.RunAsync(CreateWorkflow(), arguments, output, client, Token));
    }

    [Theory]
    [InlineData("--single-machine")]
    [InlineData("--machine worker")]
    [InlineData("--describe --single-machine")]
    [InlineData("--engine http://localhost --single-machine --single-machine")]
    public async Task Single_machine_options_reject_ambiguous_or_nonengine_modes(string arguments) =>
        await Assert.ThrowsAsync<ArgumentException>(() => RunModeDispatcher.RunAsync(CreateWorkflow(),
            arguments.Split(' '), cancellationToken: Token));

    [Fact]
    public async Task Describe_writes_exact_manifest_without_binding_required_values_or_executing_tasks()
    {
        var workflow = Workflow.Define("tests/modes", "1.2.3", w => w.Type("test")
            .Variables(v => v.Int("amount", required: true))
            .Node("work", n => n.Task("execute", _ => throw new InvalidOperationException("must not execute"))));
        using var output = new StringWriter();
        Assert.Equal(0, await RunModeDispatcher.RunAsync(workflow, ["--describe"], output, cancellationToken: Token));
        Assert.Equal(ManifestWriter.Write(workflow) + Environment.NewLine, output.ToString());
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public async Task Default_mode_executes_real_local_tasks_and_returns_final_exit_code(bool fail, int exitCode)
    {
        var called = 0;
        var workflow = Workflow.Define("tests/modes", "1.2.3", w => w.Type("test")
            .Variables(v => v.Int("amount", required: true))
            .Node("work", n => n.Task("execute", context =>
            {
                Assert.Equal(7, context.Var<int>("amount"));
                called++;
                if (fail) throw new InvalidOperationException("expected failure");
            })));
        using var output = new StringWriter();
        Assert.Equal(exitCode, await RunModeDispatcher.RunAsync(workflow, ["--var", "amount=7"], output, cancellationToken: Token));
        Assert.Equal(1, called);
        Assert.Contains(fail ? "Failed" : "Succeeded", output.ToString());
    }

    [Fact]
    public async Task Dry_run_mode_uses_mocks_and_shared_variable_parsing_without_original_tasks()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wf-mode-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, """{"default":{"outcome":"Succeed"},"variables":{"amount":2}}""", Token);
        try
        {
            var workflow = Workflow.Define("tests/modes", "1.2.3", w => w.Type("test")
                .Variables(v => v.Int("amount", required: true))
                .Node("work", n => n.Task("execute", _ => throw new InvalidOperationException("must not execute"))));
            using var output = new StringWriter();
            Assert.Equal(0, await RunModeDispatcher.RunAsync(workflow, ["--var", "amount=7", "--dry-run", path], output, cancellationToken: Token));
            using var document = JsonDocument.Parse(output.ToString());
            Assert.Equal("Succeeded", document.RootElement.GetProperty("snapshot").GetProperty("status").GetString());
            Assert.Equal(7, document.RootElement.GetProperty("snapshot").GetProperty("variables").GetProperty("amount").GetInt32());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Engine_mode_posts_typed_values_version_and_idempotency_key_without_running_tasks()
    {
        var runId = Guid.CreateVersion7();
        var keys = new HashSet<Guid>();
        using var client = new HttpClient(new Handler(async (request, token) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("http://localhost:1234/api/v1/runs", request.RequestUri!.ToString());
            Assert.True(keys.Add(Guid.Parse(Assert.Single(request.Headers.GetValues("Idempotency-Key")))));
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Assert.Equal("tests/modes", payload.RootElement.GetProperty("workflowId").GetString());
            Assert.Equal("1.2.3", payload.RootElement.GetProperty("version").GetString());
            var values = payload.RootElement.GetProperty("variables");
            Assert.Equal(7, values.GetProperty("amount").GetInt32());
            Assert.True(values.GetProperty("enabled").GetBoolean());
            Assert.Equal("a=b", values.GetProperty("name").GetString());
            Assert.Equal("01:00:00", values.GetProperty("duration").GetString());
            Assert.Equal("token", values.GetProperty("secret").GetProperty("Name").GetString());
            Assert.Equal(new[] { "a", "b" }, values.GetProperty("names").EnumerateArray().Select(value => value.GetString()));
            return Accepted(runId);
        }));
        var workflow = Workflow.Define("tests/modes", "1.2.3", w => w.Type("test")
            .Variables(v => v.Int("amount", required: true).Bool("enabled").String("name")
                .Duration("duration").Secret("secret").StringList("names"))
            .Node("work", n => n.Task("execute", _ => throw new InvalidOperationException("must not execute"))));
        for (var index = 0; index < 2; index++)
        {
            using var output = new StringWriter();
            Assert.Equal(0, await RunModeDispatcher.RunAsync(workflow,
                ["--var", "amount=7", "--engine", "http://localhost:1234", "--var", "enabled=true",
                    "--var", "name=a=b", "--var", "duration=01:00:00", "--var", "secret=token", "--var", """names=["a","b"]"""],
                output, client, Token));
            Assert.Equal(runId + Environment.NewLine, output.ToString());
        }
        Assert.Equal(2, keys.Count);
    }

    [Theory]
    [InlineData("http://remote.invalid")]
    [InlineData("ftp://localhost")]
    [InlineData("https://user:password@localhost")]
    [InlineData("https://localhost/prefix")]
    [InlineData("https://localhost?query=1")]
    [InlineData("https://localhost#fragment")]
    [InlineData("not-a-url")]
    public async Task Invalid_engine_origins_fail_before_any_network_or_task_execution(string origin)
    {
        using var client = new HttpClient(new Handler((_, _) => throw new InvalidOperationException("network must not run")));
        await Assert.ThrowsAsync<ArgumentException>(() => RunModeDispatcher.RunAsync(CreateWorkflow(), ["--engine", origin],
            httpClient: client, cancellationToken: Token));
    }

    [Theory]
    [InlineData("missing=3")]
    [InlineData("amount=not-json")]
    [InlineData("amount=true")]
    public async Task Invalid_engine_variables_fail_before_any_network_or_task_execution(string variable)
    {
        using var client = new HttpClient(new Handler((_, _) => throw new InvalidOperationException("network must not run")));
        await Assert.ThrowsAsync<ArgumentException>(() => RunModeDispatcher.RunAsync(CreateWorkflow(),
            ["--engine", "http://localhost", "--var", variable], httpClient: client, cancellationToken: Token));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.OK)]
    public async Task Engine_rejection_does_not_print_a_success_id_or_execute_locally(HttpStatusCode status)
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(status))));
        using var output = new StringWriter();
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => RunModeDispatcher.RunAsync(CreateWorkflow(),
            ["--engine", "http://localhost"], output, client, Token));
        Assert.Equal(status, error.StatusCode);
        Assert.Equal("", output.ToString());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("""{"runId":"00000000-0000-0000-0000-000000000000"}""")]
    public async Task Engine_must_return_a_nonempty_run_id(string json)
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        })));
        using var output = new StringWriter();
        await Assert.ThrowsAsync<InvalidDataException>(() => RunModeDispatcher.RunAsync(CreateWorkflow(),
            ["--engine", "http://localhost"], output, client, Token));
        Assert.Equal("", output.ToString());
    }

    [Fact]
    public async Task Oversized_engine_response_and_inflight_cancellation_propagate_without_success_output()
    {
        using var output = new StringWriter();
        using (var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted)
        {
            Content = new StringContent(new string(' ', 65537))
        }))))
            await Assert.ThrowsAsync<HttpRequestException>(() => RunModeDispatcher.RunAsync(CreateWorkflow(),
                ["--engine", "http://localhost"], output, client, Token));
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var began = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var client = new HttpClient(new Handler(async (_, token) =>
        {
            began.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("unreachable");
        })))
        {
            var pending = RunModeDispatcher.RunAsync(CreateWorkflow(), ["--engine", "http://localhost"], output, client, stopping.Token);
            await began.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            await stopping.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        Assert.Equal("", output.ToString());
    }

    public static TheoryData<string[]> InvalidOptions => new()
    {
        new[] { "--engine" }, new[] { "--dry-run" }, new[] { "--var" }, new[] { "--socket" }, new[] { "--task-host" },
        new[] { "--socket", "unused" }, new[] { "--describe", "--describe" }, new[] { "--describe", "--engine", "http://localhost" },
        new[] { "--engine", "http://localhost", "--dry-run", "unused" }, new[] { "--task-host", "--describe" },
        new[] { "--describe", "--var", "amount=1" }, new[] { "--task-host", "--socket", "unused", "--var", "amount=1" },
        new[] { "--task-host", "--socket", "first", "--socket", "second" }, new[] { "--var", "amount=1", "--var", "amount=2" },
        new[] { "--unknown" }, new[] { "--var", "--describe" }, new[] { "--engine", "" }
    };

    [Theory]
    [MemberData(nameof(InvalidOptions))]
    public async Task Invalid_or_conflicting_options_fail_explicitly(string[] args) =>
        await Assert.ThrowsAnyAsync<ArgumentException>(() => RunModeDispatcher.RunAsync(CreateWorkflow(), args, cancellationToken: Token));

    private static WorkflowDefinition CreateWorkflow() => Workflow.Define("tests/modes", "1.2.3", w => w.Type("test")
        .Variables(v => v.Int("amount", 1))
        .Node("work", n => n.Task("execute", _ => throw new InvalidOperationException("must not execute"))));

    private static HttpResponseMessage Accepted(Guid runId) => new(HttpStatusCode.Accepted)
    {
        Content = new StringContent($$"""{"runId":"{{runId:D}}"}""", Encoding.UTF8, "application/json")
    };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
