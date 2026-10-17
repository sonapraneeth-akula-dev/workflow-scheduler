using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Sdk;

namespace Wf.Sdk.Tests;

public sealed class RemoteOperationRunnerTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static HttpOperationTask Operation(bool dedup = false, bool lookup = false, string? secret = null,
        HttpOperationStatusContract? status = null) => Tasks.HttpOperation("https://remote.test/jobs",
        status ?? new("state", TimeSpan.FromDays(2), resultField: "value", errorField: "reason"),
        deduplication: dedup ? new("Idempotency-Key", TimeSpan.FromDays(2)) : null,
        lookup: lookup ? new("https://remote.test/lookup/{submissionKey}", TimeSpan.FromDays(2)) : null,
        secretReference: secret, body: """{"private":"request-body"}""");

    [Theory]
    [InlineData(200, """{"state":"Succeeded","value":42}""")]
    [InlineData(202, """{"state":"Succeeded","value":42}""")]
    [InlineData(200, """{"value":42}""")]
    [InlineData(204, "")]
    public async Task Durable_intent_is_acknowledged_before_POST_and_immediate_terminal_completes(int status, string body)
    {
        var fixture = new Runner((request, harness) =>
        {
            Assert.Equal(1, harness.Saves);
            Assert.Equal("SubmissionPending", harness.State!.Stage);
            Assert.Equal(HttpMethod.Post, request.Method);
            return Task.FromResult(Reply(status, body));
        });
        await fixture.Run(Operation(dedup: true));
        Assert.Equal(1, fixture.Requests);
        Assert.Equal(1, fixture.Saves);
        Assert.StartsWith("wf-", Assert.Single(fixture.Keys));
        Assert.DoesNotContain("request-body", Encoding.UTF8.GetString(fixture.Bytes!));
        if (status != 204) Assert.Equal(42, fixture.Context.Outputs.Get<int>("result"));
    }

    [Fact]
    public async Task Failed_durable_intent_cannot_submit()
    {
        var store = new CheckpointStore((_, _, _, _) => throw new IOException("checkpoint unavailable"));
        var context = new TaskContext(Token, taskKey: "node/task", runId: Guid.NewGuid(), checkpoint: store);
        using var client = new HttpClient(new Handler((_, _) => throw new InvalidOperationException("network called")));
        await Assert.ThrowsAsync<IOException>(() => RemoteOperationRunner.ExecuteAsync(Operation(), context, client));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lost_submission_and_lookup_miss_never_POST_again_without_dedup(bool lookup)
    {
        var fixture = new Runner((request, _) => request.Method == HttpMethod.Post
            ? throw new HttpRequestException("token-and-private-url")
            : Task.FromResult(Reply(404, "not JSON or safe to echo")));
        var operation = Operation(lookup: lookup);
        var lost = await Assert.ThrowsAsync<TaskFailureException>(() => fixture.Run(operation));
        Assert.True(lost.Retryable);
        Assert.DoesNotContain("private-url", lost.Message);
        fixture.Resume();
        var unknown = await Assert.ThrowsAsync<TaskFailureException>(() => fixture.Run(operation));
        Assert.False(unknown.Retryable);
        Assert.StartsWith("RemoteOutcomeUnknown:", unknown.Message);
        Assert.Equal(lookup ? 2 : 1, fixture.Requests);
        Assert.Single(fixture.Keys);
    }

    [Fact]
    public async Task Dedup_reuses_same_key_across_attempts_after_lost_submission()
    {
        var fixture = new Runner((_, harness) => harness.Requests == 1
            ? throw new HttpRequestException("lost")
            : Task.FromResult(Reply(200, """{"state":"Succeeded","value":19}""")));
        var operation = Operation(dedup: true, lookup: true);
        await Assert.ThrowsAsync<TaskFailureException>(() => fixture.Run(operation));
        fixture.Handle = (request, _) => Task.FromResult(request.Method == HttpMethod.Get
            ? Reply(404) : Reply(200, """{"state":"Succeeded","value":19}"""));
        fixture.Resume();
        await fixture.Run(operation);
        Assert.Equal(3, fixture.Requests);
        Assert.Equal(2, fixture.Keys.Count);
        Assert.Equal(fixture.Keys[0], fixture.Keys[1]);
        Assert.Equal(19, fixture.Context.Outputs.Get<int>("result"));
    }

    [Fact]
    public async Task Authoritative_lookup_found_reattaches_without_POST_even_without_state_in_reference_reply()
    {
        var fixture = new Runner((_, _) => throw new HttpRequestException("lost"));
        var operation = Operation(lookup: true);
        await Assert.ThrowsAsync<TaskFailureException>(() => fixture.Run(operation));
        fixture.Handle = (request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            return Task.FromResult(request.RequestUri!.AbsolutePath.StartsWith("/lookup/", StringComparison.Ordinal)
                ? Reply(200, "", "/status/one", retryAfter: TimeSpan.FromSeconds(1))
                : Reply(200, """{"state":"Succeeded","value":23}"""));
        };
        fixture.Resume();
        await fixture.Run(operation);
        Assert.Equal(3, fixture.Requests);
        Assert.Single(fixture.Keys);
        Assert.Equal("RemoteSubmitted", fixture.State!.Stage);
        Assert.Equal(23, fixture.Context.Outputs.Get<int>("result"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Poll_respects_RetryAfter_delta_and_date_then_resumes_submitted_checkpoint_without_POST(bool date)
    {
        var fixture = new Runner((_, harness) =>
        {
            var response = Reply(202, """{"state":"Pending"}""", "/status/one");
            response.Headers.RetryAfter = date
                ? new RetryConditionHeaderValue(harness.Clock.GetUtcNow().AddSeconds(20))
                : new RetryConditionHeaderValue(TimeSpan.FromSeconds(20));
            return Task.FromResult(response);
        });
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        fixture.Resume(cancellation: cancellation.Token);
        var operation = Operation(dedup: true);
        var pending = fixture.Run(operation);
        await Until(() => fixture.Saves == 2);
        Assert.Equal(1, fixture.Requests);
        var at = fixture.State!.NextPollAt!.Value;
        fixture.Clock.Advance(at - fixture.Clock.GetUtcNow() - TimeSpan.FromMilliseconds(1));
        Assert.Equal(1, fixture.Requests);
        fixture.Handle = (request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            throw new HttpRequestException("poll response lost");
        };
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(1));
        await Assert.ThrowsAsync<TaskFailureException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5), Token));
        fixture.Resume();
        fixture.Handle = (request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            return Task.FromResult(Reply(200, """{"state":"Succeeded","value":7}"""));
        };
        await fixture.Run(operation);
        Assert.Single(fixture.Keys);
        Assert.Equal(3, fixture.Requests);
        Assert.Equal(2, fixture.Saves);
        Assert.Equal(7, fixture.Context.Outputs.Get<int>("result"));
    }

    [Theory]
    [InlineData(302, "", "https://remote.test/other")]
    [InlineData(202, "{}", "https://evil.test/status")]
    [InlineData(202, "{}", "/status?token=never-echo")]
    [InlineData(202, "{}", "/status%0d%0a")]
    [InlineData(202, """{"state":"Alien"}""", "/status")]
    [InlineData(202, """{"state":"Pending","state":"Succeeded"}""", "/status")]
    [InlineData(202, "not-json-never-echo", "/status")]
    [InlineData(202, "{}", null)]
    public async Task Unsafe_or_ambiguous_protocol_fails_sanitized_without_followup(int code, string body, string? location)
    {
        var fixture = new Runner((_, _) => Task.FromResult(Reply(code, body, location)));
        var error = await Assert.ThrowsAsync<TaskFailureException>(() => fixture.Run(Operation()));
        Assert.False(error.Retryable);
        Assert.Equal(FailureCategory.ValidationError, error.Category);
        Assert.DoesNotContain("never-echo", error.Message);
        Assert.Equal(1, fixture.Requests);
        Assert.Equal(1, fixture.Saves);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Expired_or_mismatched_checkpoint_fails_closed_before_any_network(bool mismatch)
    {
        var fixture = new Runner((_, _) => throw new HttpRequestException("lost"));
        var operation = Operation(dedup: true, lookup: true);
        await Assert.ThrowsAsync<TaskFailureException>(() => fixture.Run(operation));
        if (!mismatch) fixture.Clock.Advance(TimeSpan.FromDays(3));
        fixture.Resume(runId: mismatch ? Guid.NewGuid() : null);
        var error = await Assert.ThrowsAsync<TaskFailureException>(() => fixture.Run(operation));
        Assert.False(error.Retryable);
        Assert.StartsWith(mismatch ? "RemoteOperationInvalidCheckpoint:" : "RemoteOutcomeUnknown:", error.Message);
        Assert.Equal(1, fixture.Requests);
    }

    [Fact]
    public async Task Declared_ID_template_and_existing_secret_are_used_without_persisting_credentials()
    {
        var fixture = new Runner((request, harness) =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("private-token", request.Headers.Authorization.Parameter);
            return Task.FromResult(harness.Requests == 1
                ? Reply(202, """{"state":"Pending","id":"one/segment"}""")
                : Reply(200, """{"state":"Succeeded","value":11}"""));
        });
        fixture.Resume(secrets: new Dictionary<string, string> { ["remote"] = "private-token" });
        await fixture.Run(Operation(secret: "remote", status: new("state", TimeSpan.FromDays(2),
            locationHeader: null, operationIdField: "id", urlTemplate: "https://remote.test/status/{operationId}",
            resultField: "value")));
        Assert.Equal("https://remote.test/status/one%2Fsegment", fixture.State!.StatusUrl);
        Assert.DoesNotContain("private-token", Encoding.UTF8.GetString(fixture.Bytes!));
        Assert.Equal(11, fixture.Context.Outputs.Get<int>("result"));
    }

    [Theory]
    [InlineData("Failed", "RemoteOperationFailed:")]
    [InlineData("Cancelled", "RemoteOperationCancelled:")]
    public async Task Remote_terminal_failure_is_nonretryable_with_declared_error_output(string state, string prefix)
    {
        var fixture = new Runner((_, _) => Task.FromResult(Reply(200,
            "{\"state\":\"" + state + "\",\"reason\":{\"code\":\"service-error\"}}")));
        var error = await Assert.ThrowsAsync<TaskFailureException>(() => fixture.Run(Operation()));
        Assert.StartsWith(prefix, error.Message);
        Assert.False(error.Retryable);
        Assert.Equal("service-error", fixture.Context.Outputs.Values["error"].GetProperty("code").GetString());
    }

    [Fact]
    public async Task Cancellation_while_waiting_stops_without_another_request_and_keeps_submitted_state()
    {
        var fixture = new Runner((_, _) => Task.FromResult(Reply(202, "{}", "/status", TimeSpan.FromHours(1))));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        fixture.Resume(cancellation: cancellation.Token);
        var pending = fixture.Run(Operation());
        await Until(() => fixture.Saves == 2);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, fixture.Requests);
        Assert.Equal("RemoteSubmitted", fixture.State!.Stage);
    }

    [Fact]
    public async Task Response_body_is_bounded()
    {
        var fixture = new Runner((_, _) => Task.FromResult(Reply(200, new string('x', NodeOutputs.MaxBytes + 1))));
        var error = await Assert.ThrowsAsync<TaskFailureException>(() => fixture.Run(Operation()));
        Assert.False(error.Retryable);
        Assert.Equal(1, fixture.Requests);
    }

    [Theory]
    [InlineData(404)]
    [InlineData(410)]
    public async Task Lost_retained_status_is_unknown_not_a_resubmission(int status)
    {
        var fixture = new Runner((_, harness) => Task.FromResult(harness.Requests == 1
            ? Reply(202, "{}", "/status") : Reply(status)));
        var error = await Assert.ThrowsAsync<TaskFailureException>(() => fixture.Run(Operation(dedup: true)));
        Assert.StartsWith("RemoteOutcomeUnknown:", error.Message);
        Assert.False(error.Retryable);
        Assert.Single(fixture.Keys);
        Assert.Equal("RemoteSubmitted", fixture.State!.Stage);
    }

    [Fact]
    public async Task Request_timeout_is_bounded_and_keeps_pending_identity_for_reconciliation()
    {
        var clock = new ManualTimeProvider();
        using var client = new HttpClient(new Handler(async (_, stop) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stop);
            throw new InvalidOperationException("unreachable");
        }));
        var store = new CheckpointStore((_, _, _, _) => Task.CompletedTask, timeProvider: clock);
        var context = new TaskContext(Token, checkpoint: store, runId: Guid.NewGuid(), taskKey: "node/task");
        var running = RemoteOperationRunner.ExecuteAsync(Operation(), context, client, clock);
        clock.Advance(TimeSpan.FromSeconds(30));
        var error = await Assert.ThrowsAsync<TaskFailureException>(() => running.WaitAsync(TimeSpan.FromSeconds(5), Token));
        Assert.StartsWith("RemoteOperationTransport:", error.Message);
        Assert.True(error.Retryable);
        Assert.Equal("SubmissionPending", store.TryLoad(RemoteOperationJsonContext.Default.RemoteOperationState)!.Stage);
    }

    [Fact]
    public async Task Malformed_checkpoint_is_sanitized_and_cannot_contact_service()
    {
        var clock = new ManualTimeProvider();
        var store = new CheckpointStore((_, _, _, _) => Task.CompletedTask,
            Encoding.UTF8.GetBytes("""{"Stage":"SubmissionPending","private":"never-echo"}"""),
            1, clock.GetUtcNow(), clock);
        var context = new TaskContext(Token, checkpoint: store, runId: Guid.NewGuid(), taskKey: "node/task");
        using var client = new HttpClient(new Handler((_, _) => throw new InvalidOperationException("network called")));
        var error = await Assert.ThrowsAsync<TaskFailureException>(() =>
            RemoteOperationRunner.ExecuteAsync(Operation(), context, client, clock));
        Assert.StartsWith("RemoteOperationInvalidCheckpoint:", error.Message);
        Assert.DoesNotContain("never-echo", error.Message);
    }

    private static HttpResponseMessage Reply(int code, string body = "", string? location = null, TimeSpan? retryAfter = null)
    {
        var reply = new HttpResponseMessage((HttpStatusCode)code) { Content = new StringContent(body) };
        if (location is not null) reply.Headers.TryAddWithoutValidation("Location", location);
        if (retryAfter is { } delay) reply.Headers.RetryAfter = new RetryConditionHeaderValue(delay);
        return reply;
    }

    private static async Task Until(Func<bool> condition)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        stop.CancelAfter(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(1, stop.Token);
    }

    private sealed class Runner
    {
        private readonly Guid runId = Guid.NewGuid();
        public ManualTimeProvider Clock { get; } = new();
        public Func<HttpRequestMessage, Runner, Task<HttpResponseMessage>> Handle { get; set; }
        public TaskContext Context { get; private set; } = null!;
        public byte[]? Bytes { get; private set; }
        public RemoteOperationState? State => Bytes is null ? null :
            JsonSerializer.Deserialize(Bytes, RemoteOperationJsonContext.Default.RemoteOperationState);
        public int Saves { get; private set; }
        public int Requests { get; private set; }
        public List<string> Keys { get; } = [];
        private DateTimeOffset savedAt;

        public Runner(Func<HttpRequestMessage, Runner, Task<HttpResponseMessage>> handle)
        {
            Handle = handle;
            Resume();
        }

        public void Resume(Guid? runId = null, CancellationToken? cancellation = null,
            IReadOnlyDictionary<string, string>? secrets = null)
        {
            var store = new CheckpointStore((bytes, _, at, _) =>
            {
                Bytes = bytes.ToArray();
                savedAt = at;
                Clock.Advance(TimeSpan.FromSeconds(5));
                Saves++;
                return Task.CompletedTask;
            }, initialState: Bytes ?? ReadOnlyMemory<byte>.Empty, sequence: Saves,
                savedAt: Bytes is null ? null : savedAt, timeProvider: Clock);
            Context = new(cancellation ?? Token, checkpoint: store, runId: runId ?? this.runId,
                taskKey: "node/task", attempt: new((uint)Saves + 1), secrets: secrets);
        }

        public async Task Run(HttpOperationTask operation)
        {
            using var client = new HttpClient(new Handler((request, _) =>
            {
                Requests++;
                if (request.Method == HttpMethod.Post)
                    Keys.Add(request.Headers.TryGetValues("Idempotency-Key", out var keys) ? keys.Single() : "(no dedup header)");
                return Handle(request, this);
            }));
            await RemoteOperationRunner.ExecuteAsync(operation, Context, client, Clock);
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handle(request, cancellationToken);
    }
}
