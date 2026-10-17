using System.Net.Sockets;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Host.V1;
using Wf.Contracts;
using Wf.Sdk.Hosting;

namespace Wf.Sdk.Tests;

public sealed class TaskHostServerTests
{
    [Fact]
    public async Task Host_binds_once_rejects_wrong_attempt_and_waits_for_result_acknowledgement()
    {
        var invoked = 0;
        var abort = new AbortRecord("operator", "Operator", DateTimeOffset.UtcNow,
            "Stop the run; preserve cleanup context.", AbortSource.API);
        AbortRecord? observedAbort = null;
        var workflow = Workflow.Define("test/host/protocol", "1", w => w.Type("test")
            .Node("work", n => n.Task("task", async ctx =>
            {
                observedAbort = ctx.Abort;
                Interlocked.Increment(ref invoked);
                await Task.Delay(Timeout.InfiniteTimeSpan, ctx.CancellationToken);
            })));
        var directory = Path.Combine(Path.GetTempPath(), $"wf-t-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var socketPath = Path.Combine(directory, "s");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var server = TaskHostServer.RunAsync(workflow, socketPath, stop.Token);
        using var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, token) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            }
        };
        using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = handler });
        var client = new TaskHost.TaskHostClient(channel);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            var ready = await client.ReadyAsync(new ReadyRequest(), cancellationToken: deadline.Token);
            Assert.False(ready.Bound);
            Assert.Equal(4u, ready.ProtocolVersion);
            Assert.Equal((uint)Environment.ProcessId, ready.ProcessId);
            var attempt = new AttemptRef { RunId = Guid.NewGuid().ToString(), NodeKey = "work", TaskKey = "task", Attempt = 1 };
            var request = new ExecuteRequest
            {
                Dispatch = new Dispatch
                {
                    Task = attempt, Version = "1",
                    AbortJson = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes(abort,
                        AbortRecordJsonContext.Default.AbortRecord)),
                    DeadlineAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(30))
                }
            };
            Assert.Empty((await client.RequiredSecretsAsync(request, cancellationToken: deadline.Token)).Names);
            Assert.Equal(0, Volatile.Read(ref invoked));
            Assert.False((await client.ReadyAsync(new ReadyRequest(), cancellationToken: deadline.Token)).Bound);
            using var execution = client.ExecuteAsync(request, cancellationToken: deadline.Token);
            while (!(await client.ReadyAsync(new ReadyRequest(), cancellationToken: deadline.Token)).Bound)
                await Task.Delay(5, deadline.Token);
            await RejectAsync(StatusCode.FailedPrecondition, () => client.ExecuteAsync(request, cancellationToken: deadline.Token).ResponseAsync);
            await RejectAsync(StatusCode.FailedPrecondition, () => client.RequiredSecretsAsync(request, cancellationToken: deadline.Token).ResponseAsync);
            await RejectAsync(StatusCode.FailedPrecondition, () => client.AcknowledgeAsync(
                new AcknowledgeRequest { Task = attempt }, cancellationToken: deadline.Token).ResponseAsync);
            var wrong = attempt.Clone();
            wrong.Attempt++;
            await RejectAsync(StatusCode.NotFound, () => client.CancelAsync(
                new CancelRequest { Task = wrong }, cancellationToken: deadline.Token).ResponseAsync);
            await client.CancelAsync(new CancelRequest { Task = attempt }, cancellationToken: deadline.Token);
            Assert.Equal(TaskOutcome.Cancelled, (await execution.ResponseAsync).Outcome);
            Assert.Equal(1, Volatile.Read(ref invoked));
            Assert.Equal(abort, observedAbort);
            Assert.False(server.IsCompleted);
            await RejectAsync(StatusCode.FailedPrecondition, () => client.ExecuteAsync(request, cancellationToken: deadline.Token).ResponseAsync);
            await RejectAsync(StatusCode.NotFound, () => client.AcknowledgeAsync(
                new AcknowledgeRequest { Task = wrong }, cancellationToken: deadline.Token).ResponseAsync);
            await client.AcknowledgeAsync(new AcknowledgeRequest { Task = attempt }, cancellationToken: deadline.Token);
            Assert.Equal(0, await server.WaitAsync(deadline.Token));
        }
        finally
        {
            await stop.CancelAsync();
            await server;
            if (File.Exists(socketPath)) File.Delete(socketPath);
            Directory.Delete(directory);
        }
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("")]
    public async Task Socket_path_must_be_nonempty_and_absolute(string path)
    {
        var workflow = Workflow.Define("test/host/path", "1", w => w.Type("test").Node("n", n => n.Task("t", _ => { })));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => TaskHostServer.RunAsync(workflow, path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Existing_socket_file_is_not_deleted_or_replaced()
    {
        var workflow = Workflow.Define("test/host/path", "1", w => w.Type("test").Node("n", n => n.Task("t", _ => { })));
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "preserve", TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<IOException>(() => TaskHostServer.RunAsync(workflow, path, TestContext.Current.CancellationToken));
            Assert.Equal("preserve", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        }
        finally { File.Delete(path); }
    }

    private static async Task RejectAsync(StatusCode status, Func<Task> call)
    {
        var exception = await Assert.ThrowsAsync<RpcException>(call);
        Assert.Equal(status, exception.StatusCode);
    }
}
