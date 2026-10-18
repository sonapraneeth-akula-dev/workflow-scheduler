using System.Diagnostics;
using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Wf.Agent.Logs;
using Wf.Agent.Secrets;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Logs;
using Wf.ControlPlane.Api;
using Wf.ControlPlane.Auth;
using Wf.ControlPlane.Storage;
using Wf.ControlPlane.Live;
using Wf.Integration.Tests.Agents;

namespace Wf.Integration.Tests.Logs;

public sealed class TaskLogsIntegrationTests(TaskLogsDatabase database) : IClassFixture<TaskLogsDatabase>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TaskLogs_actual_openapi_export_declares_json_page_and_ndjson_stream_matching_HTTP_bodies()
    {
        await using var host = await TaskLogsHost.Start(database.DataSource);
        using var exported = await host.Client.GetAsync("/openapi/v1.json", Token);
        Assert.Equal(HttpStatusCode.OK, exported.StatusCode);
        using var document = JsonDocument.Parse(await exported.Content.ReadAsStringAsync(Token));
        var root = document.RootElement;
        var response = root.GetProperty("paths").GetProperty("/api/v1/runs/{id}/tasks/{task}/{attempt}/logs")
            .GetProperty("get").GetProperty("responses").GetProperty("200");
        var content = response.GetProperty("content");
        Assert.Equal(["application/json", "application/x-ndjson"],
            content.EnumerateObject().Select(item => item.Name).Order(StringComparer.Ordinal));
        Assert.Equal("#/components/schemas/TaskLogsPage",
            content.GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
        AssertType(content.GetProperty("application/x-ndjson").GetProperty("schema"), "string");
        var schemas = root.GetProperty("components").GetProperty("schemas");
        var page = schemas.GetProperty("TaskLogsPage").GetProperty("properties");
        AssertType(page.GetProperty("records"), "array");
        Assert.Equal("#/components/schemas/TaskLogRecord",
            page.GetProperty("records").GetProperty("items").GetProperty("$ref").GetString());
        AssertType(page.GetProperty("matches"), "array");
        Assert.Equal("#/components/schemas/TaskLogMatch",
            page.GetProperty("matches").GetProperty("items").GetProperty("$ref").GetString());
        var record = schemas.GetProperty("TaskLogRecord").GetProperty("properties");
        var match = schemas.GetProperty("TaskLogMatch").GetProperty("properties");
        AssertType(record.GetProperty("line"), "integer");
        AssertType(match.GetProperty("index"), "integer");
        AssertType(match.GetProperty("length"), "integer");
        Assert.Equal("#/components/schemas/LogFrame", record.GetProperty("frame").GetProperty("$ref").GetString());
        Assert.Equal("#/components/schemas/LogFrame", match.GetProperty("frame").GetProperty("$ref").GetString());
        AssertType(schemas.GetProperty("LogFrame").GetProperty("properties").GetProperty("text"), "string");

        var attempt = await Seed(host.Group);
        await host.Capture(attempt, ["contract 😀 ***"]);
        using var range = await host.Get(attempt);
        var ranged = (await range.Content.ReadFromJsonAsync<TaskLogsPage>(Token))!;
        Assert.Equal("contract 😀 ***", Assert.Single(ranged.Records).Frame.Text);
        using var search = await host.Get(attempt, "?search=contract");
        var searched = (await search.Content.ReadFromJsonAsync<TaskLogsPage>(Token))!;
        Assert.Equal(8, Assert.Single(searched.Matches).Length);
        using var download = await host.Get(attempt, "?download=true", completion: HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal("application/x-ndjson", download.Content.Headers.ContentType!.MediaType);
        var frame = JsonSerializer.Deserialize((await download.Content.ReadAsStringAsync(Token)).TrimEnd('\n'),
            LogJsonContext.Default.LogFrame)!;
        Assert.Equal("contract 😀 ***", frame.Text);

        static void AssertType(JsonElement schema, string expected)
        {
            var type = schema.GetProperty("type");
            if (type.ValueKind == JsonValueKind.Array)
                Assert.Contains(type.EnumerateArray(), item => item.GetString() == expected);
            else Assert.Equal(expected, type.GetString());
        }
    }

    [Fact]
    public async Task TaskLogs_actual_spool_signed_upload_range_search_download_and_no_postgres_payload()
    {
        await using var host = await TaskLogsHost.Start(database.DataSource);
        var attempt = await Seed(host.Group);
        await host.Capture(attempt, ["head 😀\r\nsecret", "", "stderr secret", "tail"], ["secret"]);
        using var range = await host.Get(attempt, "?offset=1&length=2");
        Assert.Equal(HttpStatusCode.OK, range.StatusCode);
        var page = (await range.Content.ReadFromJsonAsync<TaskLogsPage>(Token))!;
        Assert.Equal([2L, 3L], page.Records.Select(item => item.Line));
        Assert.Equal(["", "stderr ***"], page.Records.Select(item => item.Frame.Text));
        Assert.Empty(page.Matches);
        Assert.True(range.Headers.CacheControl!.NoStore);
        Assert.Equal("nosniff", Assert.Single(range.Headers.GetValues("X-Content-Type-Options")));
        using var search = await host.Get(attempt, "?search=" + Uri.EscapeDataString(@"\*{3}"));
        var hits = (await search.Content.ReadFromJsonAsync<TaskLogsPage>(Token))!;
        Assert.Equal([1L, 3L], hits.Matches.Select(item => item.Line));
        Assert.All(hits.Matches, hit => Assert.Equal(3, hit.Length));
        using var download = await host.Get(attempt, "?download=true");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("application/x-ndjson", download.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition!.DispositionType);
        var frames = (await download.Content.ReadAsStringAsync(Token)).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => JsonSerializer.Deserialize(value, LogJsonContext.Default.LogFrame)!).ToArray();
        Assert.Equal(["head 😀\r\n***", "", "stderr ***", "tail"], frames.Select(frame => frame.Text));
        await using var inspect = database.DataSource.CreateCommand("""
            SELECT row_to_json(a)::text FROM task_attempts a WHERE run_id=$1
            """);
        inspect.Parameters.AddWithValue(Guid.Parse(attempt.RunId));
        var stored = (string)(await inspect.ExecuteScalarAsync(Token))!;
        Assert.DoesNotContain("head", stored);
        Assert.DoesNotContain("secret", stored);
        Assert.DoesNotContain("***", stored);
        Assert.DoesNotContain("log-upload", await range.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task TaskLogs_canonical_unicode_and_literal_percent_keys_preserve_interrupted_capture_evidence()
    {
        await using var host = await TaskLogsHost.Start(database.DataSource);
        var attempt = await Seed(host.Group, node: "n😀", task: "t%2Ftail");
        await host.Capture(attempt, ["committed 😀 ***\r\n"], interrupted: true);
        using var response = await host.Get(attempt);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = (await response.Content.ReadFromJsonAsync<TaskLogsPage>(Token))!;
        Assert.True(page.CaptureInterrupted);
        Assert.Equal("committed 😀 ***\r\n", Assert.Single(page.Records).Frame.Text);
        using var download = await host.Get(attempt, "?download=true");
        Assert.Equal("true", Assert.Single(download.Headers.GetValues("X-Log-Capture-Interrupted")));
        Assert.DoesNotContain("t%2Ftail", download.Content.Headers.ContentDisposition!.FileName);
    }

    [Fact]
    public async Task TaskLogs_fresh_viewer_auth_hides_unknown_foreign_attempt_and_blob_existence()
    {
        await using var host = await TaskLogsHost.Start(database.DataSource);
        var own = await Seed(host.Group);
        var foreign = await Seed(Guid.NewGuid());
        await host.Capture(own, ["private log"]);
        foreach (var attempt in new[]
        {
            foreign, withAttempt(2), withTask("other"), new AttemptRef
            { RunId = Guid.CreateVersion7().ToString(), NodeKey = "node", TaskKey = "task", Attempt = 1 }
        })
        {
            var reads = host.Store.Reads;
            using var denied = await host.Get(attempt);
            await AssertProblem(denied, HttpStatusCode.Forbidden);
            Assert.Equal(reads, host.Store.Reads);
            Assert.DoesNotContain("private log", await denied.Content.ReadAsStringAsync(Token));
        }
        using (var anonymous = await host.Get(own, authenticated: false))
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using (var workload = await host.Get(own, token: host.Jwt(identity: "app", role: "Agent")))
            await AssertProblem(workload, HttpStatusCode.Forbidden);
        using (var overage = await host.Get(own, token: host.Jwt(overage: true)))
            await AssertProblem(overage, HttpStatusCode.Forbidden);
        await using var revoke = database.DataSource.CreateCommand("DELETE FROM role_bindings WHERE principal_id=$1");
        revoke.Parameters.AddWithValue(host.Group);
        await revoke.ExecuteNonQueryAsync(Token);
        using var revoked = await host.Get(own);
        await AssertProblem(revoked, HttpStatusCode.Forbidden);

        AttemptRef withAttempt(uint number) => new() { RunId = own.RunId, NodeKey = own.NodeKey, TaskKey = own.TaskKey, Attempt = number };
        AttemptRef withTask(string task) => new() { RunId = own.RunId, NodeKey = own.NodeKey, TaskKey = task, Attempt = own.Attempt };
    }

    [Theory]
    [InlineData("?length=0")]
    [InlineData("?offset=-1")]
    [InlineData("?offset=1&offset=2")]
    [InlineData("?download=true&download=true")]
    [InlineData("?search=a&length=1")]
    [InlineData("?cursor=bad&search=a")]
    [InlineData("?download=1")]
    [InlineData("?unknown=x")]
    [InlineData("?search=%28")]
    public async Task TaskLogs_invalid_and_duplicate_queries_return_problems(string query)
    {
        await using var host = await TaskLogsHost.Start(database.DataSource);
        var attempt = await Seed(host.Group);
        using var response = await host.Get(attempt, query);
        await AssertProblem(response, HttpStatusCode.BadRequest);
        Assert.Equal(0, host.Store.Reads);
    }

    [Theory]
    [InlineData("node%252Ftask", "1")]
    [InlineData("node%2F..%5Csecret", "1")]
    [InlineData("node%2Ftask", "0")]
    [InlineData("node%2Ftask", "01")]
    [InlineData("node%2Ftask", "32768")]
    public async Task TaskLogs_malformed_route_identity_fails_before_storage(string task, string attempt)
    {
        await using var host = await TaskLogsHost.Start(database.DataSource);
        var seeded = await Seed(host.Group);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/runs/{seeded.RunId}/tasks/{task}/{attempt}/logs");
        request.Headers.Authorization = new("Bearer", host.Jwt());
        using var response = await host.Client.SendAsync(request, Token);
        await AssertProblem(response, HttpStatusCode.BadRequest);
        Assert.Equal(0, host.Store.Reads);
    }

    [Fact]
    public async Task TaskLogs_search_caps_1000_and_cursor_resumes_same_record_with_utf16_indices()
    {
        await using var host = await TaskLogsHost.Start(database.DataSource);
        var attempt = await Seed(host.Group);
        await host.Capture(attempt, ["😀" + new string('a', 1205), "last a"]);
        using var response = await host.Get(attempt, "?search=a");
        var first = (await response.Content.ReadFromJsonAsync<TaskLogsPage>(Token))!;
        Assert.Equal(1000, first.Matches.Count);
        Assert.Equal(2, first.Matches[0].Index);
        Assert.Equal(1001, first.Matches[^1].Index);
        Assert.NotNull(first.NextCursor);
        using var next = await host.Get(attempt, "?search=a&cursor=" + first.NextCursor);
        var second = (await next.Content.ReadFromJsonAsync<TaskLogsPage>(Token))!;
        Assert.Equal(207, second.Matches.Count);
        Assert.Equal(1002, second.Matches[0].Index);
        Assert.Null(second.NextCursor);
        using var empty = await host.Get(attempt, "?search=" + Uri.EscapeDataString("$"));
        var emptyHits = (await empty.Content.ReadFromJsonAsync<TaskLogsPage>(Token))!;
        Assert.Equal(2, emptyHits.Matches.Count);
    }

    [Fact]
    public async Task TaskLogs_pathological_regex_and_blocked_storage_obey_total_ten_second_budget()
    {
        await using var host = await TaskLogsHost.Start(database.DataSource);
        var attempt = await Seed(host.Group);
        await host.Capture(attempt, [new string('a', 16000) + "!"]);
        var clock = Stopwatch.StartNew();
        using (var response = await host.Get(attempt, "?search=" + Uri.EscapeDataString("^(a+)+$")))
            await AssertProblem(response, HttpStatusCode.UnprocessableEntity);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10));
        host.Store.BlockIndex = true;
        clock.Restart();
        using (var response = await host.Get(attempt, "?search=a"))
            await AssertProblem(response, HttpStatusCode.GatewayTimeout);
        Assert.InRange(clock.Elapsed.TotalSeconds, 9, 12);
        Assert.True(host.Store.Canceled.Task.IsCompleted);
    }

    [Fact]
    public async Task TaskLogs_actual_head_tail_retention_never_returns_excluded_original_gzip_bytes()
    {
        await using var host = await TaskLogsHost.Start(database.DataSource);
        var attempt = await Seed(host.Group);
        await host.Capture(attempt, Enumerable.Range(0, 50).Select(number => $"{number:D2} " + new string((char)('A' + number % 26), 200)),
            options: new LogSpoolOptions(MaxBytes: 2000, HeadBytes: 600, TailBytes: 600));
        var index = await new TaskLogReader(host.Store).ReadIndexAsync(attempt, Token);
        Assert.True(index.Truncated);
        Assert.True(index.DroppedLines > 0);
        Assert.Contains(index.Chunks.GroupBy(item => item.Number), group => group.Count() == 2);
        var retained = new List<TaskLogBytes>();
        await foreach (var frame in new TaskLogReader(host.Store).ReadAsync(index, token: Token)) retained.Add(frame);
        var expected = retained.Where(item => !item.Marker).ToArray();
        Assert.True(expected.Length < 50);
        using var range = await host.Get(attempt, "?length=1000");
        var page = (await range.Content.ReadFromJsonAsync<TaskLogsPage>(Token))!;
        Assert.Equal(expected.Select(item => item.Frame.Text), page.Records.Select(item => item.Frame.Text));
        Assert.Equal(50 - expected.Length, page.DroppedLines);
        using var download = await host.Get(attempt, "?download=true");
        var text = await download.Content.ReadAsStringAsync(Token);
        Assert.Equal(Encoding.UTF8.GetString(retained.SelectMany(item => item.Bytes).ToArray()), text);
        Assert.Equal(1, text.Split("log records dropped").Length - 1);
        using var excluded = await host.Get(attempt, "?search=" + Uri.EscapeDataString("^25 "));
        Assert.Empty((await excluded.Content.ReadFromJsonAsync<TaskLogsPage>(Token))!.Matches);
        Assert.DoesNotContain("25 ", text);
    }

    [Fact]
    public async Task TaskLogs_large_download_returns_partial_body_before_later_chunk_and_cancels_cleanly()
    {
        await using var host = await TaskLogsHost.Start(database.DataSource);
        var attempt = await Seed(host.Group);
        await host.Capture(attempt, Enumerable.Range(0, 400).Select(number => number + ":" + new string('x', 65500)));
        var index = await new TaskLogReader(host.Store).ReadIndexAsync(attempt, Token);
        Assert.True(index.Bytes > 25_000_000);
        Assert.True(index.Chunks.Count >= 4);
        host.Store.BlockAfterChunk = 1;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        using var response = await host.GetCancelableDownload(attempt, cancel.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var input = await response.Content.ReadAsStreamAsync(Token);
        var partial = new byte[4096];
        Assert.True(await input.ReadAsync(partial, Token) > 0);
        Assert.True(host.Store.ChunkReads <= 2, "Download read the whole log before returning a partial body.");
        Assert.Contains("0:", Encoding.UTF8.GetString(partial));
        var consume = input.CopyToAsync(Stream.Null, cancel.Token);
        await host.Store.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => consume);
        await input.DisposeAsync();
        response.Dispose();
        await host.Store.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        for (var wait = 0; host.Store.ActiveReads > 0 && wait < 100; wait++) await Task.Delay(10, Token);
        Assert.Equal(0, host.Store.ActiveReads);
        host.Store.BlockAfterChunk = int.MaxValue;
        host.Store.ChunkReads = 0;
        using var retry = await host.Get(attempt, "?offset=399&length=1");
        var page = (await retry.Content.ReadFromJsonAsync<TaskLogsPage>(Token))!;
        Assert.StartsWith("399:", Assert.Single(page.Records).Frame.Text);
        Assert.Equal(1, host.Store.ChunkReads);
        using var complete = await host.Get(attempt, "?download=true", completion: HttpCompletionOption.ResponseHeadersRead);
        using var records = new StreamReader(await complete.Content.ReadAsStreamAsync(Token));
        for (var number = 0; number < 400; number++)
        {
            var record = JsonSerializer.Deserialize((await records.ReadLineAsync(Token))!, LogJsonContext.Default.LogFrame)!;
            Assert.StartsWith(number + ":", record.Text);
        }
        Assert.Null(await records.ReadLineAsync(Token));
    }

    [Theory]
    [InlineData("missing", 404)]
    [InlineData("active", 409)]
    [InlineData("incomplete", 409)]
    [InlineData("json", 502)]
    [InlineData("foreign", 502)]
    [InlineData("gzip", 502)]
    [InlineData("gzip-invalid", 502)]
    [InlineData("missing-chunk", 404)]
    [InlineData("oversized-index", 502)]
    public async Task TaskLogs_missing_inprogress_corrupt_storage_never_looks_like_empty_success(string fault, int status)
    {
        await using var host = await TaskLogsHost.Start(database.DataSource);
        var attempt = await Seed(host.Group, active: fault == "active");
        if (fault is not ("missing" or "active"))
        {
            await host.Capture(attempt, ["evidence"]);
            var path = host.IndexPath(attempt);
            var index = await new TaskLogReader(host.Store).ReadIndexAsync(attempt, Token);
            if (fault == "json") await File.WriteAllTextAsync(path, "{\"corrupt\":", Token);
            if (fault == "foreign") await File.WriteAllBytesAsync(path,
                JsonSerializer.SerializeToUtf8Bytes(index with { Prefix = "logs/other/" }, LogJsonContext.Default.LogIndex), Token);
            if (fault == "incomplete") await File.WriteAllBytesAsync(path,
                JsonSerializer.SerializeToUtf8Bytes(index with { Complete = false }, LogJsonContext.Default.LogIndex), Token);
            if (fault == "gzip")
                await File.WriteAllBytesAsync(host.BlobPath(index.Prefix + index.Chunks[0].Name), [1, 2, 3], Token);
            if (fault == "gzip-invalid")
            {
                var chunk = index.Chunks[0];
                var invalid = new byte[checked((int)chunk.CompressedBytes)];
                await File.WriteAllBytesAsync(host.BlobPath(index.Prefix + chunk.Name), invalid, Token);
                index = index with { Chunks = [chunk with { Sha256 = Convert.ToHexStringLower(SHA256.HashData(invalid)) }] };
                await File.WriteAllBytesAsync(path, JsonSerializer.SerializeToUtf8Bytes(index, LogJsonContext.Default.LogIndex), Token);
            }
            if (fault == "missing-chunk") File.Delete(host.BlobPath(index.Prefix + index.Chunks[0].Name));
            if (fault == "oversized-index")
            {
                await using var file = File.OpenWrite(path);
                file.SetLength(TaskLogReader.MaximumIndexBytes + 1L);
            }
        }
        using var response = await host.Get(attempt);
        await AssertProblem(response, (HttpStatusCode)status);
        Assert.DoesNotContain("records", await response.Content.ReadAsStringAsync(Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TaskLogs_download_corruption_is_problem_before_headers_or_aborted_after_partial_body(bool laterChunk)
    {
        await using var host = await TaskLogsHost.Start(database.DataSource);
        var attempt = await Seed(host.Group);
        await host.Capture(attempt, Enumerable.Range(0, laterChunk ? 130 : 1)
            .Select(number => number + ":" + new string('x', 65500)));
        var index = await new TaskLogReader(host.Store).ReadIndexAsync(attempt, Token);
        await File.WriteAllBytesAsync(host.BlobPath(index.Prefix + index.Chunks[laterChunk ? 1 : 0].Name), [1, 2, 3], Token);
        using var response = await host.Get(attempt, "?download=true", completion: HttpCompletionOption.ResponseHeadersRead);
        if (!laterChunk)
        {
            await AssertProblem(response, HttpStatusCode.BadGateway);
            Assert.Null(response.Content.Headers.ContentDisposition);
            return;
        }
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var input = await response.Content.ReadAsStreamAsync(Token);
        var buffer = new byte[4096];
        Assert.True(await input.ReadAsync(buffer, Token) > 0);
        await Assert.ThrowsAnyAsync<IOException>(() => input.CopyToAsync(Stream.Null, Token));
    }

    private async Task<AttemptRef> Seed(Guid group, bool active = false, string node = "node", string task = "task")
    {
        var team = "logs" + Guid.NewGuid().ToString("N");
        var run = Guid.CreateVersion7();
        await using var seed = database.DataSource.CreateCommand("""
            INSERT INTO teams(id,name) VALUES (@team,@team);
            INSERT INTO workflow_types(id,team_id,name,normalized_name,description,metadata_schema)
              VALUES (@team||'/type',@team,'type','type','logs','{}');
            INSERT INTO role_bindings(id,scope_kind,scope_id,role,principal_id,principal_kind)
              VALUES (@binding,1,@team,0,@principal,0);
            INSERT INTO runs(run_day,id,workflow_id,version_id,trigger,triggered_by,variables,metadata,status,created_at,deadline_at)
              VALUES (uuid_v7_run_day(@run),@run,@team||'/type/workflow',@version,0,'logs','{}','{}',0,now(),now()+interval '1 hour');
            INSERT INTO task_attempts(run_day,run_id,task_key,attempt,status,deadline_at)
              VALUES (uuid_v7_run_day(@run),@run,@taskKey,1,@status,now()+interval '1 hour');
            """);
        seed.Parameters.AddWithValue("team", team);
        seed.Parameters.AddWithValue("binding", Guid.CreateVersion7());
        seed.Parameters.AddWithValue("principal", group);
        seed.Parameters.AddWithValue("run", run);
        seed.Parameters.AddWithValue("version", Guid.CreateVersion7());
        seed.Parameters.AddWithValue("status", (short)(active ? 1 : 5));
        seed.Parameters.AddWithValue("taskKey", node + "/" + task);
        await seed.ExecuteNonQueryAsync(Token);
        return new() { RunId = run.ToString(), NodeKey = node, TaskKey = task, Attempt = 1 };
    }

    private static async Task AssertProblem(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        Assert.Equal((int)expected, problem.RootElement.GetProperty("status").GetInt32());
        var body = problem.RootElement.GetRawText();
        foreach (var secret in new[] { "logs/", "index.json", "log-upload", ".log.gz", "C:\\" })
            Assert.DoesNotContain(secret, body);
    }
}

public sealed class TaskLogsDatabase : AgentDatabase
{
    protected override IReadOnlyList<string> Migrations =>
        ["0002_runs_events.sql", "0003_attempt_timers.sql", "0010_commands.sql",
            "0015_team_type_registry.sql", "0016_role_bindings.sql"];
}

internal sealed class TaskLogsHost : IAsyncDisposable
{
    private readonly WebApplication app;
    private readonly string root;
    private readonly SymmetricSecurityKey signing;
    private readonly LogUploadCapabilities capabilities;
    private readonly ConcurrentQueue<Exception> failures;
    public Guid Group { get; } = Guid.NewGuid();
    public HttpClient Client { get; }
    public TaskLogsStore Store { get; }
    public IServiceProvider Services => app.Services;

    private TaskLogsHost(WebApplication app, string root, TaskLogsStore store, LogUploadCapabilities capabilities,
        SymmetricSecurityKey signing, ConcurrentQueue<Exception> failures)
    {
        this.app = app;
        this.root = root;
        Store = store;
        this.signing = signing;
        this.capabilities = capabilities;
        this.failures = failures;
        Client = new() { BaseAddress = new(app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.Single()), Timeout = TimeSpan.FromSeconds(60) };
    }

    public static async Task<TaskLogsHost> Start(NpgsqlDataSource source, bool live = false, TaskLogsStore? sharedStore = null)
    {
        var root = Path.Combine(Directory.GetCurrentDirectory(), ".task-logs-work", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = sharedStore ?? new TaskLogsStore(new FileSystemBlobStore(Path.Combine(root, "blobs"), RandomNumberGenerator.GetBytes(32)));
        var capabilities = new LogUploadCapabilities(RandomNumberGenerator.GetBytes(32), TimeProvider.System);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole().SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(source);
        builder.Services.AddSingleton<IBlobStore>(store);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(capabilities);
        builder.Services.AddSingleton(new BlobUploadGrants(RandomNumberGenerator.GetBytes(32)));
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, TaskLogsJsonContext.Default));
        // Runtime registration needs no XML-comment interceptors in the test assembly.
        var documentName = "v1";
        builder.Services.AddOpenApi(documentName);
        var key = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(64));
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new()
            {
                ValidateIssuer = true, ValidIssuer = "task-logs", ValidateAudience = true, ValidAudience = "task-logs",
                ValidateLifetime = true, IssuerSigningKey = key, ValidateIssuerSigningKey = true, RoleClaimType = "roles"
            };
        });
        builder.Services.AddTeamAuthorization();
        if (live) builder.Services.AddWorkflowLive().AddWorkflowLiveLogs();
        var app = builder.Build();
        var failures = new ConcurrentQueue<Exception>();
        app.Use(async (context, next) =>
        {
            try { await next(context); }
            catch (Exception error) { failures.Enqueue(error); throw; }
        });
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapTaskLogsEndpoints();
        app.MapLogUploadEndpoints();
        app.MapOpenApi();
        if (live) app.MapHub<LiveHub>(LiveHub.Path);
        await app.StartAsync(TestContext.Current.CancellationToken);
        if (live)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            while (!app.Services.GetRequiredService<LiveLogRelay>().IsReady) await Task.Delay(10, timeout.Token);
        }
        return new(app, root, store, capabilities, key, failures);
    }

    public string Jwt(string identity = "user", string? role = null, bool overage = false)
    {
        var claims = new List<Claim> { new("oid", Guid.NewGuid().ToString()), new("idtyp", identity),
            new("scp", "logs.read"), new("groups", Group.ToString()) };
        if (role is not null) claims.Add(new("roles", role));
        if (overage) claims.Add(new("hasgroups", "true"));
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("task-logs", "task-logs", claims,
            expires: DateTime.UtcNow.AddMinutes(5), signingCredentials: new(signing, SecurityAlgorithms.HmacSha256)));
    }

    public Task<HttpResponseMessage> Get(AttemptRef attempt, string query = "", bool authenticated = true, string? token = null,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead) =>
        GetCore(TestContext.Current.CancellationToken, attempt, query, authenticated, token, completion);

    public Task<HttpResponseMessage> GetCancelableDownload(AttemptRef attempt, CancellationToken cancellationToken) =>
        GetCore(cancellationToken, attempt, "?download=true", true, null, HttpCompletionOption.ResponseHeadersRead);

    private Task<HttpResponseMessage> GetCore(CancellationToken cancellationToken, AttemptRef attempt, string query,
        bool authenticated, string? token, HttpCompletionOption completion)
    {
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/v1/runs/{attempt.RunId}/tasks/{Uri.EscapeDataString(attempt.NodeKey + "/" + attempt.TaskKey)}/{attempt.Attempt}/logs{query}");
        if (authenticated) request.Headers.Authorization = new("Bearer", token ?? Jwt());
        return Send();
        async Task<HttpResponseMessage> Send()
        {
            using (request)
            {
                var response = await Client.SendAsync(request, completion, cancellationToken);
                if (response.StatusCode == HttpStatusCode.InternalServerError)
                {
                    response.Dispose();
                    throw new InvalidOperationException("Unexpected HTTP 500 in the task log fixture.",
                        failures.TryDequeue(out var error) ? error : null);
                }
                return response;
            }
        }
    }

    public async Task Capture(AttemptRef attempt, IEnumerable<string> lines, string[]? secrets = null,
        LogSpoolOptions? options = null, bool interrupted = false)
    {
        await using var spool = new LogSpooler(Path.Combine(root, "spool"), attempt, new SecretMasker(secrets ?? []), options);
        foreach (var line in lines) await spool.WriteAsync("stdout", line, cancellationToken: TestContext.Current.CancellationToken);
        await spool.CompleteAsync(interrupted);
        var uploader = new LogChunkUploader(Client, TimeProvider.System, NullLogger<LogChunkUploader>.Instance);
        await uploader.UploadAsync(spool, capabilities.Create(attempt, DateTimeOffset.UtcNow.AddHours(1)),
            Client.BaseAddress!, TestContext.Current.CancellationToken);
    }

    public string IndexPath(AttemptRef attempt) => BlobPath(LogIndex.PrefixFor(attempt) + "index.json");
    public string BlobPath(string key) => Path.Combine(root, "blobs", key.Replace('/', Path.DirectorySeparatorChar));

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
        Store.Dispose();
        capabilities.Dispose();
        Directory.Delete(root, true);
    }
}

internal sealed class TaskLogsStore(FileSystemBlobStore inner) : IBlobStore, IDisposable
{
    public int Reads, ChunkReads, ActiveReads;
    public bool BlockIndex;
    public int BlockAfterChunk = int.MaxValue;
    public TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task UploadAsync(BlobContent content, Stream source, CancellationToken cancellationToken = default) =>
        inner.UploadAsync(content, source, cancellationToken);
    public Task DeleteLiveLogAsync(string key, CancellationToken cancellationToken = default) =>
        inner.DeleteLiveLogAsync(key, cancellationToken);
    public async Task<Stream> OpenLogReadAsync(string key, long maximumBytes, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref Reads);
        if (BlockIndex) await Block(cancellationToken);
        return await inner.OpenLogReadAsync(key, maximumBytes, cancellationToken);
    }
    public async Task DownloadAsync(BlobContent content, Stream destination, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref Reads);
        Interlocked.Increment(ref ActiveReads);
        try
        {
            if (Interlocked.Increment(ref ChunkReads) > BlockAfterChunk) await Block(cancellationToken);
            await inner.DownloadAsync(content, destination, cancellationToken);
        }
        finally { Interlocked.Decrement(ref ActiveReads); }
    }
    private async Task Block(CancellationToken token)
    {
        Blocked.TrySetResult();
        try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
        catch (OperationCanceledException) { Canceled.TrySetResult(); throw; }
    }
    public void Dispose() => inner.Dispose();
}
