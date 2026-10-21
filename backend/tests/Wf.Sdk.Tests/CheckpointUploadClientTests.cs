using System.Net;
using Google.Protobuf.WellKnownTypes;
using Wf.Contracts;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Checkpoints;
using Wf.Contracts.Definition;

namespace Wf.Sdk.Tests;

public sealed class CheckpointUploadClientTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(503, true)]
    [InlineData(502, true)]
    [InlineData(403, true)]
    [InlineData(408, true)]
    [InlineData(429, true)]
    [InlineData(400, false)]
    [InlineData(413, false)]
    public async Task Rejected_checkpoint_is_a_bounded_platform_failure_and_never_acknowledges_or_loads_state(
        int status, bool retryable)
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status))));
        var acknowledgements = 0;
        var checkpoint = CheckpointUploadClient.Create(http, Dispatch(), (_, _) =>
        {
            acknowledgements++;
            return Task.CompletedTask;
        }, Token);
        var error = await Assert.ThrowsAsync<TaskFailureException>(() => checkpoint.SaveAsync(7, Token));
        Assert.Equal(FailureCategory.PlatformError, error.Category);
        Assert.Equal(retryable, error.Retryable);
        Assert.Equal($"Checkpoint storage rejected state (HTTP {status}).", error.Message);
        Assert.Equal(0, acknowledgements);
        Assert.Equal(0, checkpoint.TryLoad<int>());
    }

    [Theory]
    [InlineData("network")]
    [InlineData("io")]
    [InlineData("timeout")]
    public async Task Transport_failure_is_retryable_but_caller_cancellation_is_not_reclassified(string kind)
    {
        Exception failure = kind switch
        {
            "network" => new HttpRequestException("network"),
            "io" => new IOException("transport"),
            _ => new OperationCanceledException("HTTP timeout")
        };
        using var http = new HttpClient(new Handler((_, _) => Task.FromException<HttpResponseMessage>(failure)));
        var checkpoint = CheckpointUploadClient.Create(http, Dispatch(), (_, _) => throw new InvalidOperationException("No ACK expected."), Token);
        var error = await Assert.ThrowsAsync<TaskFailureException>(() => checkpoint.SaveAsync(7, Token));
        Assert.Equal(FailureCategory.PlatformError, error.Category);
        Assert.True(error.Retryable);
        Assert.Same(failure, error.InnerException);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Token);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => checkpoint.SaveAsync(8, cancelled.Token));
    }

    [Fact]
    public async Task Created_state_becomes_visible_only_after_the_durable_agent_acknowledgement()
    {
        using var http = new HttpClient(new Handler((request, _) =>
        {
            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("a.b", request.Headers.Authorization.Parameter);
            Assert.True(request.Headers.Contains(CheckpointProtocol.MetadataHeader));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created));
        }));
        var ack = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checkpoint = CheckpointUploadClient.Create(http, Dispatch(), (_, token) => ack.Task.WaitAsync(token), Token);
        var saving = checkpoint.SaveAsync(7, Token);
        Assert.False(saving.IsCompleted);
        Assert.Equal(0, checkpoint.TryLoad<int>());
        ack.SetResult();
        await saving;
        Assert.Equal(7, checkpoint.TryLoad<int>());
    }

    private static Dispatch Dispatch()
    {
        var dispatch = new Dispatch
        {
            Task = new() { RunId = Guid.CreateVersion7().ToString("D"), NodeKey = "work", TaskKey = "task", Attempt = 1 },
            ReservationEpoch = 1
        };
        dispatch.Grants.Add(new UploadGrant
        {
            Kind = "checkpoints", UploadUri = "https://controller.example.invalid" + CheckpointProtocol.EndpointPath,
            MaxBytes = CheckpointProtocol.MaximumBytes, BearerToken = "a.b",
            ExpiresAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddHours(1))
        });
        return dispatch;
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
