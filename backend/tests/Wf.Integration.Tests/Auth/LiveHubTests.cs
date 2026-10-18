using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Wf.Contracts;
using Wf.Contracts.Events;
using Wf.Contracts.Registry;
using Wf.ControlPlane.Engine.Events;
using Wf.ControlPlane.Live;

namespace Wf.Integration.Tests.Auth;

public sealed partial class AuthorizationMatrixTests
{
    [Fact]
    public async Task LiveHub_MessagePack_delivers_only_committed_events_in_order_to_authorized_scopes()
    {
        var group = Guid.NewGuid();
        var seed = await Seed(RegistryRole.Viewer, group, true);
        await using var server = await Server.Start(database.DataSource, live: true);
        await using var client = await MessagePackClient.Connect(server, server.Token(group: group));
        Assert.Null(await client.Subscribe("run", seed.Run.ToString("D").ToUpperInvariant()));
        foreach (var (kind, id) in seed.OtherTeam)
            Assert.Equal(403, (await client.Subscribe(kind, id))!.Status);
        Assert.Equal(400, (await client.Subscribe("run", "invalid"))!.Status);
        foreach (var (kind, id) in seed.InScope.Where(scope => scope.Item1 != "run"))
            Assert.Null(await client.Subscribe(kind, id));

        var first = Change(seed.Run, 1);
        await using (var connection = await database.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken))
        await using (var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken))
        {
            await WriteEvent(connection, first);
            await Task.Delay(350, TestContext.Current.CancellationToken);
            Assert.False(client.Runs.Reader.TryPeek(out _));
            Assert.False(client.Lists.Reader.TryPeek(out _));
            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }
        await using var appender = new GroupCommitAppender(database.DataSource, NullLogger<GroupCommitAppender>.Instance);
        var expected = Enumerable.Range(1, 24).Select(sequence => Change(seed.Run, sequence)).ToArray();
        await appender.AppendAsync(expected, TestContext.Current.CancellationToken);
        var received = new List<LiveEvent>();
        while (received.Count < expected.Length)
        {
            var delta = await client.ReadRun();
            Assert.Equal(seed.Run.ToString("D"), delta.RunId);
            received.AddRange(delta.Events);
        }
        Assert.Equal(expected.Select(change => change.Sequence), received.Select(change => change.Seq));
        Assert.All(received, change => Assert.Null(change.Abort));
        var lists = new List<ListDelta>();
        while (lists.Count < 3) lists.Add(await client.ReadList());
        Assert.Equal(new[] { "series", "team", "type" }, lists.Select(delta => delta.Scope).Order());
        Assert.All(lists, delta =>
        {
            var summary = Assert.Single(delta.Items);
            Assert.Equal(seed.Run.ToString("D"), summary.RunId);
            Assert.Equal(24, summary.LastSeq);
            Assert.Equal(seed.Type + "/workflow", summary.WorkflowId);
        });
        Assert.False(client.Runs.Reader.TryPeek(out _));
        Assert.False(client.Lists.Reader.TryPeek(out _));
        await Assert.ThrowsAsync<InvalidOperationException>(() => appender.AppendAsync(Change(seed.Run, 26),
            TestContext.Current.CancellationToken));
        await appender.AppendAsync(Change(seed.Run, 25), TestContext.Current.CancellationToken);
        Assert.Equal(25, Assert.Single((await client.ReadRun()).Events).Seq);
        await client.Connection.InvokeAsync("Unsubscribe", "run", seed.Run.ToString("D"), TestContext.Current.CancellationToken);
        await appender.AppendAsync(Change(seed.Run, 26), TestContext.Current.CancellationToken);
        await Task.Delay(350, TestContext.Current.CancellationToken);
        Assert.False(client.Runs.Reader.TryPeek(out _));
    }

    [Fact]
    public async Task LiveHub_lists_coalesce_each_run_for_at_least_250ms_while_run_scope_keeps_every_event()
    {
        var group = Guid.NewGuid();
        var seed = await Seed(RegistryRole.Viewer, group, true);
        var clock = new Wf.Sdk.Tests.ManualTimeProvider();
        await using var server = await Server.Start(database.DataSource, live: true,
            configureServices: services => services.AddSingleton<TimeProvider>(clock));
        await using var client = await MessagePackClient.Connect(server, server.Token(group: group));
        Assert.Null(await client.Subscribe("type", seed.Type));
        Assert.Null(await client.Subscribe("run", seed.Run.ToString("D")));
        await using var appender = new GroupCommitAppender(database.DataSource, NullLogger<GroupCommitAppender>.Instance);
        for (var index = 1; index <= 8; index++)
        {
            await appender.AppendAsync(Change(seed.Run, index), TestContext.Current.CancellationToken);
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        var events = new List<LiveEvent>();
        while (events.Count < 8) events.AddRange((await client.ReadRun()).Events);
        Assert.Equal(Enumerable.Range(1, 8).Select(value => (long)value), events.Select(change => change.Seq));
        await WaitForCoalescingTimer();
        clock.Advance(TimeSpan.FromMilliseconds(249));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(client.Lists.Reader.TryPeek(out _));
        clock.Advance(TimeSpan.FromMilliseconds(1));
        var first = await client.ReadList();
        Assert.Equal(8, Assert.Single(first.Items).LastSeq);
        for (var index = 9; index <= 16; index++)
            await appender.AppendAsync(Change(seed.Run, index), TestContext.Current.CancellationToken);
        while (events.Count < 16) events.AddRange((await client.ReadRun()).Events);
        Assert.Equal(Enumerable.Range(1, 16).Select(value => (long)value), events.Select(change => change.Seq));
        await WaitForCoalescingTimer();
        clock.Advance(TimeSpan.FromMilliseconds(249));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(client.Lists.Reader.TryPeek(out _));
        clock.Advance(TimeSpan.FromMilliseconds(1));
        var next = await client.ReadList();
        Assert.Equal(16, Assert.Single(next.Items).LastSeq);
        clock.Advance(TimeSpan.FromMilliseconds(250));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(client.Lists.Reader.TryPeek(out _));

        async Task WaitForCoalescingTimer()
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            while (clock.PendingTimerCount == 0) await Task.Delay(10, timeout.Token);
        }
    }

    [Fact]
    public async Task LiveHub_registry_revocation_removes_all_existing_groups_without_resubscribing_or_token_renewal()
    {
        var group = Guid.NewGuid();
        var seed = await Seed(RegistryRole.Viewer, group, true);
        await using var server = await Server.Start(database.DataSource, live: true);
        await using var client = await MessagePackClient.Connect(server, server.Token(group: group));
        Assert.Null(await client.Subscribe("run", seed.Run.ToString("D")));
        Assert.Null(await client.Subscribe("type", seed.Type));
        await Save(new(seed.Team, [new(seed.Team, "release", "description", new([]))]));
        var reset = new[] { await client.ReadResync(), await client.ReadResync() };
        Assert.Equal(new[] { "run", "type" }, reset.Select(message => message.Scope).Order());
        await using var appender = new GroupCommitAppender(database.DataSource, NullLogger<GroupCommitAppender>.Instance);
        await appender.AppendAsync(Change(seed.Run, 1), TestContext.Current.CancellationToken);
        await Task.Delay(350, TestContext.Current.CancellationToken);
        Assert.False(client.Runs.Reader.TryPeek(out _));
        Assert.False(client.Lists.Reader.TryPeek(out _));
        Assert.Equal(403, (await client.Subscribe("run", seed.Run.ToString("D")))!.Status);
    }

    [Fact]
    public async Task LiveHub_notification_reconnect_requests_resync_and_then_delivers_the_next_committed_change()
    {
        var group = Guid.NewGuid();
        var seed = await Seed(RegistryRole.Viewer, group, true);
        await using var server = await Server.Start(database.DataSource, live: true);
        await using var client = await MessagePackClient.Connect(server, server.Token(group: group));
        Assert.Null(await client.Subscribe("run", seed.Run.ToString("D")));
        await using var kill = database.DataSource.CreateCommand("""
            SELECT pg_terminate_backend(pid) FROM pg_stat_activity
            WHERE datname=current_database() AND pid<>pg_backend_pid() AND application_name='wf-live-feed'
            """);
        Assert.Equal(true, await kill.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        var reset = await client.ReadResync();
        Assert.Equal("run", reset.Scope);
        Assert.Equal(seed.Run.ToString("D"), reset.Id);
        await using var appender = new GroupCommitAppender(database.DataSource, NullLogger<GroupCommitAppender>.Instance);
        await appender.AppendAsync(Change(seed.Run, 1), TestContext.Current.CancellationToken);
        Assert.Equal(1, Assert.Single((await client.ReadRun()).Events).Seq);
    }

    [Fact]
    public async Task LiveHub_new_runs_appear_without_polling_and_overlapping_commits_keep_the_complete_event_tail_once()
    {
        var group = Guid.NewGuid();
        var seed = await Seed(RegistryRole.Viewer, group, true);
        await using var server = await Server.Start(database.DataSource, live: true);
        await using var client = await MessagePackClient.Connect(server, server.Token(group: group));
        Assert.Null(await client.Subscribe("type", seed.Type));
        var run = await Run(seed.Type, seed.Series);
        var created = Assert.Single((await client.ReadList()).Items);
        Assert.Equal(run.ToString("D"), created.RunId);
        Assert.Equal(0, created.LastSeq);
        Assert.Equal((int)RunStatus.Queued, created.Status);
        Assert.Null(await client.Subscribe("run", run.ToString("D")));
        await using var appender = new GroupCommitAppender(database.DataSource, NullLogger<GroupCommitAppender>.Instance);
        for (var batch = 0; batch < 3; batch++)
            await appender.AppendAsync(Enumerable.Range(batch * 200 + 1, 200).Select(sequence => Change(run, sequence)).ToArray(),
                TestContext.Current.CancellationToken);
        var received = new List<LiveEvent>();
        while (received.Count < 600)
        {
            var delta = await client.ReadRun();
            Assert.InRange(delta.Events.Length, 1, 512);
            received.AddRange(delta.Events);
        }
        Assert.Equal(Enumerable.Range(1, 600).Select(value => (long)value), received.Select(change => change.Seq));
        ListDelta latest;
        do { latest = await client.ReadList(); } while (latest.Items[0].LastSeq < 600);
        Assert.Equal(600, Assert.Single(latest.Items).LastSeq);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.False(client.Runs.Reader.TryPeek(out _));
    }

    [Fact]
    public async Task LiveHub_OpenAPI_emits_only_the_public_live_message_schemas_with_matching_wire_property_names()
    {
        await using var server = await Server.Start(database.DataSource);
        using var response = await server.GetPath("/openapi/v1.json", server.Token(role: "Admin"));
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        foreach (var name in new[] { "RunDelta", "ListDelta", "LiveResync", "LiveEvent", "LiveAbort", "LiveRunSummary", "LiveSubscriptionProblem" })
            Assert.True(schemas.TryGetProperty(name, out _), name);
        Assert.True(schemas.GetProperty("LiveEvent").GetProperty("properties").TryGetProperty("seq", out _));
        Assert.Contains("\"null\"", schemas.GetProperty("LiveEvent").GetProperty("properties").GetProperty("abort").GetRawText());
        Assert.Contains("\"null\"", schemas.GetProperty("RunDelta").GetProperty("properties").GetProperty("events")
            .GetProperty("items").GetProperty("properties").GetProperty("abort").GetRawText());
        Assert.True(schemas.GetProperty("LiveRunSummary").GetProperty("properties").TryGetProperty("lastSeq", out _));
        using var ui = await server.GetPath("/openapi/ui.json", server.Token(role: "Admin"));
        ui.EnsureSuccessStatusCode();
        Assert.DoesNotContain("\"RunDelta\"", await ui.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private static RunEvent Change(Guid run, int sequence) => new(run, sequence, DateTimeOffset.UtcNow,
        sequence == 1 ? EventEntity.Run : EventEntity.Task, sequence == 1 ? null : "node/task",
        sequence == 1 ? (int)RunStatus.Running : (int)ExecutionStatus.Running);

    private static async Task WriteEvent(NpgsqlConnection connection, RunEvent change)
    {
        await using var command = new NpgsqlCommand("""
            WITH inserted AS (
                INSERT INTO run_events(run_day,run_id,seq,ts,kind,entity_key,payload)
                VALUES(uuid_v7_run_day($1),$1,$2,$3,$4,$5,$6) RETURNING seq
            )
            UPDATE runs SET last_seq=inserted.seq,status=1 FROM inserted WHERE run_day=uuid_v7_run_day($1) AND id=$1
            """, connection);
        command.Parameters.AddWithValue(change.RunId);
        command.Parameters.AddWithValue(change.Sequence);
        command.Parameters.AddWithValue(change.At);
        command.Parameters.AddWithValue((short)change.Entity);
        command.Parameters.AddWithValue((object?)change.Key ?? DBNull.Value);
        command.Parameters.AddWithValue(JsonSerializer.SerializeToUtf8Bytes(change));
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private sealed class MessagePackClient(HubConnection connection) : IAsyncDisposable
    {
        public HubConnection Connection => connection;
        public Channel<RunDelta> Runs { get; } = Channel.CreateUnbounded<RunDelta>();
        public Channel<ListDelta> Lists { get; } = Channel.CreateUnbounded<ListDelta>();
        public Channel<LiveResync> Resyncs { get; } = Channel.CreateUnbounded<LiveResync>();

        public static async Task<MessagePackClient> Connect(Server server, string jwt)
        {
            var connection = new HubConnectionBuilder().WithUrl(new Uri(new Uri(server.Api), LiveHub.Path), options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(jwt);
                options.Transports = HttpTransportType.WebSockets;
                options.SkipNegotiation = true;
            }).AddMessagePackProtocol().Build();
            var client = new MessagePackClient(connection);
            connection.On<RunDelta>("RunDelta", delta => client.Runs.Writer.TryWrite(delta));
            connection.On<ListDelta>("ListDelta", delta => client.Lists.Writer.TryWrite(delta));
            connection.On<LiveResync>("Resync", delta => client.Resyncs.Writer.TryWrite(delta));
            await connection.StartAsync(TestContext.Current.CancellationToken);
            return client;
        }

        public Task<LiveSubscriptionProblem?> Subscribe(string scope, string id) =>
            connection.InvokeAsync<LiveSubscriptionProblem?>("Subscribe", scope, id, TestContext.Current.CancellationToken);
        public Task<RunDelta> ReadRun() => Runs.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        public Task<ListDelta> ReadList() => Lists.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        public Task<LiveResync> ReadResync() => Resyncs.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }
}
