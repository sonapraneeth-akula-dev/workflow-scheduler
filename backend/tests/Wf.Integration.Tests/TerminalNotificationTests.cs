using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Contracts.Events;
using Wf.ControlPlane.Api.Subscriptions;
using Wf.ControlPlane.Data;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Events;
using Wf.ControlPlane.Engine.Notifications;
using Wf.ControlPlane.Registry;
using Wf.Engine.Core;
using Wf.Integration.Tests.Execution;

namespace Wf.Integration.Tests;

public sealed class TerminalNotificationTests(RollupDatabase database) : IClassFixture<RollupDatabase>, IAsyncLifetime
{
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private readonly RunStore store = new(database.DataSource, NullLogger<RunStore>.Instance);
    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
    private const string Secret = "secret://notifications/webhook";
    private sealed record Scope(string Workflow, string Type, Guid Series, Guid Schedule, Guid Group);

    public async ValueTask InitializeAsync() => await Execute("DELETE FROM subscriptions");
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Theory]
    [InlineData(false, RunStatus.Succeeded)]
    [InlineData(false, RunStatus.Failed)]
    [InlineData(false, RunStatus.Aborted)]
    [InlineData(false, RunStatus.TimedOut)]
    [InlineData(true, RunStatus.Succeeded)]
    [InlineData(true, RunStatus.Failed)]
    [InlineData(true, RunStatus.Aborted)]
    [InlineData(true, RunStatus.TimedOut)]
    public async Task Both_actual_terminal_writers_match_all_scopes_events_and_channels_once_without_private_payloads(
        bool grouped, RunStatus status)
    {
        var scope = await Registry();
        foreach (var kind in new[] { "workflow", "schedule", "type" })
            foreach (var channel in new[] { "Webhook", "Email", "Teams" })
                await Subscribe(scope, kind, channel: channel);
        var failedOnly = await Subscribe(scope, "workflow", events: ["Failed"]);
        await Subscribe(await Registry(), "type");
        var run = await Create(scope);
        await Finish(run, status, grouped);
        var rows = await Outbox(run.Snapshot.RunId);
        Assert.Equal(status == RunStatus.Succeeded ? 9 : 10, rows.Count);
        Assert.Equal(status != RunStatus.Succeeded, rows.Any(row => row.Subscription == failedOnly));
        foreach (var row in rows)
        {
            using var json = JsonDocument.Parse(row.Payload);
            var value = json.RootElement;
            Assert.Equal("1.0", value.GetProperty("specversion").GetString());
            Assert.Equal(run.Snapshot.RunId.ToString(), value.GetProperty("id").GetString());
            Assert.Equal("urn:kardia:runs", value.GetProperty("source").GetString());
            Assert.Equal(scope.Workflow, value.GetProperty("subject").GetString());
            Assert.Equal(status == RunStatus.Succeeded ? "io.kardia.run.completed" : "io.kardia.run.failed",
                value.GetProperty("type").GetString());
            Assert.Equal("application/json", value.GetProperty("datacontenttype").GetString());
            Assert.True(DateTimeOffset.TryParse(value.GetProperty("time").GetString(), out _));
            var data = value.GetProperty("data");
            Assert.Equal(status.ToString(), data.GetProperty("status").GetString());
            Assert.Equal(scope.Series, data.GetProperty("seriesId").GetGuid());
            Assert.Equal(value.GetProperty("time").GetString(), data.GetProperty("endedAt").GetString());
            Assert.DoesNotContain("private", row.Payload);
            Assert.DoesNotContain(Secret, row.Payload);
        }
        await Execute("UPDATE runs SET status=status,ended_at=ended_at WHERE id=$1", run.Snapshot.RunId);
        Assert.Equal(rows.Count, (await Outbox(run.Snapshot.RunId)).Count);
    }

    [Fact]
    public async Task Group_commit_exact_replay_and_multiple_runs_preserve_one_outbox_per_subscription_and_run()
    {
        var scope = await Registry();
        await Subscribe(scope);
        var first = await Create(scope);
        var second = await Create(scope);
        var changes = Changes(first, RunStatus.Succeeded).Concat(Changes(second, RunStatus.Failed)).ToArray();
        await using var writer = new GroupCommitAppender(database.DataSource, NullLogger<GroupCommitAppender>.Instance);
        await writer.AppendAsync(changes, Token);
        await writer.AppendAsync(changes, Token);
        Assert.Single(await Outbox(first.Snapshot.RunId));
        Assert.Single(await Outbox(second.Snapshot.RunId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Outbox_failure_rolls_back_terminal_state_events_snapshot_and_both_rollups_before_acknowledgement(bool grouped)
    {
        var scope = await Registry();
        var subscription = await Subscribe(scope);
        var run = await Create(scope);
        var changes = Changes(run, RunStatus.Succeeded);
        if (!grouped)
            foreach (var change in changes[..^1]) await store.AppendAsync(run, change, Token);
        var before = (await store.LoadAsync(run.Snapshot.RunId, Token)).Snapshot;
        var name = "reject_notification_" + Guid.NewGuid().ToString("N");
        await Execute($"""
            CREATE FUNCTION {name}() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'injected notification storage failure'; END $$;
            CREATE TRIGGER {name} BEFORE INSERT ON notification_outbox FOR EACH ROW
                WHEN (NEW.subscription_id='{subscription}') EXECUTE FUNCTION {name}();
            """);
        try
        {
            await using var writer = new GroupCommitAppender(database.DataSource, NullLogger<GroupCommitAppender>.Instance);
            await Assert.ThrowsAsync<PostgresException>(() => grouped ? writer.AppendAsync(changes, Token) :
                store.AppendAsync(run, changes[^1], Token));
            var persisted = (await store.LoadAsync(run.Snapshot.RunId, Token)).Snapshot;
            Assert.Equal((before.Status, before.LastSequence, before.EndedAt),
                (persisted.Status, persisted.LastSequence, persisted.EndedAt));
            Assert.Empty(await Outbox(run.Snapshot.RunId));
            Assert.Equal(0L, await Scalar("SELECT count(*) FROM daily_rollups WHERE workflow_id=$1", scope.Workflow));
            Assert.Equal(0L, await Scalar("SELECT count(*) FROM dashboard_rollups WHERE workflow_id=$1", scope.Workflow));
        }
        finally { await Execute($"DROP TRIGGER {name} ON notification_outbox; DROP FUNCTION {name}()"); }
        if (grouped)
        {
            await using var writer = new GroupCommitAppender(database.DataSource, NullLogger<GroupCommitAppender>.Instance);
            await writer.AppendAsync(changes, Token);
        }
        else await store.AppendAsync(run, changes[^1], Token);
        Assert.Single(await Outbox(run.Snapshot.RunId));
    }

    [Fact]
    public async Task Other_workflows_and_other_series_within_the_same_type_do_not_receive_workflow_or_schedule_notifications()
    {
        var scope = await Registry();
        await Subscribe(scope, "workflow");
        await Subscribe(scope, "schedule");
        var typeSubscription = await Subscribe(scope, "type");
        var foreign = scope with { Workflow = scope.Type + "/other-workflow", Series = Guid.CreateVersion7() };
        await Execute("INSERT INTO workflows(id,type_id) VALUES($1,$2)", foreign.Workflow, foreign.Type);
        var run = await Create(foreign);
        await Finish(run);
        Assert.Equal(typeSubscription, Assert.Single(await Outbox(run.Snapshot.RunId)).Subscription);
    }

    [Fact]
    public async Task No_notification_is_created_for_active_runs_or_for_subscriptions_created_after_completion()
    {
        var scope = await Registry();
        var run = await Create(scope);
        Assert.Empty(await Outbox(run.Snapshot.RunId));
        await Finish(run);
        await Subscribe(scope);
        await Execute("UPDATE runs SET status=status,ended_at=ended_at WHERE id=$1", run.Snapshot.RunId);
        Assert.Empty(await Outbox(run.Snapshot.RunId));
        var next = await Create(scope);
        await Finish(next);
        Assert.Single(await Outbox(next.Snapshot.RunId));
    }

    [Fact]
    public async Task Real_user_subscription_and_terminal_commit_reach_real_webhook_with_valid_signature_after_503_retry_under_30_seconds()
    {
        await using var receiver = await Receiver.Start(Token, attempt => attempt == 1 ? 503 : 204);
        var scope = await Registry();
        await using var api = await MetricsApiServer.Start(database.DataSource, Token);
        var request = new SubscriptionRequest("workflow", scope.Workflow, ["Completed"], "Webhook", receiver.Target, Secret);
        using var created = await api.Send(HttpMethod.Post, "/api/v1/subscriptions", api.Jwt(scope.Group),
            new StringContent(JsonSerializer.Serialize(request, SubscriptionJson.Default.SubscriptionRequest), Encoding.UTF8, "application/json"), Token);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var options = Options(scope, receiver.Target);
        using var dispatcher = Dispatcher(options);
        var run = await Create(scope);
        var started = Stopwatch.StartNew();
        await Finish(run);
        await dispatcher.StartAsync(Token);
        try
        {
            var first = await receiver.Next(Token);
            var second = await receiver.Next(Token);
            Assert.True(started.Elapsed < TimeSpan.FromSeconds(30), $"Actual delivery took {started.Elapsed}.");
            VerifySignature(first);
            VerifySignature(second);
            Assert.Equal(first.Body, second.Body);
            using var value = JsonDocument.Parse(second.Body);
            Assert.Equal(run.Snapshot.RunId, value.RootElement.GetProperty("id").GetGuid());
            var deadline = Stopwatch.StartNew();
            while (!(await State(run.Snapshot.RunId)).Delivered)
            {
                Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(10));
                await Task.Delay(20, Token);
            }
            var row = await State(run.Snapshot.RunId);
            Assert.Equal(2, row.Attempts);
            Assert.Null(row.Error);
            Assert.False(row.Failed);
        }
        finally { await dispatcher.StopAsync(Token); }
    }

    [Theory]
    [InlineData(500)]
    [InlineData(503)]
    [InlineData(599)]
    [InlineData(408)]
    [InlineData(429)]
    public async Task Retryable_HTTP_failures_stay_durable_not_due_until_backoff_and_resume_on_a_new_dispatcher(int status)
    {
        await using var receiver = await Receiver.Start(Token, count => count == 1 ? status : 200);
        var scope = await Registry();
        await Subscribe(scope, target: receiver.Target);
        var run = await Create(scope);
        await Finish(run);
        var clock = new Clock();
        using var options = Options(scope, receiver.Target);
        using (var first = Dispatcher(options, clock)) Assert.True(await first.DispatchOneAsync(Token));
        var retry = await State(run.Snapshot.RunId);
        Assert.Equal((1, false, false, "http_" + status.ToString(CultureInfo.InvariantCulture)),
            (retry.Attempts, retry.Delivered, retry.Failed, retry.Error));
        Assert.Equal(clock.GetUtcNow().AddSeconds(1), retry.Next);
        using var restarted = Dispatcher(options, clock);
        Assert.False(await restarted.DispatchOneAsync(Token));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(await restarted.DispatchOneAsync(Token));
        Assert.True((await State(run.Snapshot.RunId)).Delivered);
        Assert.Equal(2, receiver.Deliveries.Count);
    }

    [Theory]
    [InlineData(301)]
    [InlineData(307)]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    public async Task Permanent_HTTP_failures_and_redirects_are_not_retried_or_followed(int status)
    {
        await using var forbidden = await Receiver.Start(Token);
        await using var receiver = await Receiver.Start(Token, _ => status, redirect: forbidden.Target);
        var scope = await Registry();
        await Subscribe(scope, target: receiver.Target);
        var run = await Create(scope);
        await Finish(run);
        using var options = Options(scope, receiver.Target);
        using var dispatcher = Dispatcher(options);
        Assert.True(await dispatcher.DispatchOneAsync(Token));
        Assert.False(await dispatcher.DispatchOneAsync(Token));
        var row = await State(run.Snapshot.RunId);
        Assert.True(row.Failed);
        Assert.False(row.Delivered);
        Assert.Equal("http_" + status.ToString(CultureInfo.InvariantCulture), row.Error);
        Assert.Single(receiver.Deliveries);
        Assert.Empty(forbidden.Deliveries);
    }

    [Fact]
    public async Task Attempt_limit_is_terminal_and_a_poison_webhook_does_not_block_other_subscriptions()
    {
        await using var receiver = await Receiver.Start(Token, _ => 503);
        var scope = await Registry();
        await Subscribe(scope, target: receiver.Target);
        var run = await Create(scope);
        await Finish(run);
        await Execute("UPDATE notification_outbox SET attempts=7 WHERE run_id=$1", run.Snapshot.RunId);
        using var options = Options(scope, receiver.Target);
        using var dispatcher = Dispatcher(options);
        Assert.True(await dispatcher.DispatchOneAsync(Token));
        var failed = await State(run.Snapshot.RunId);
        Assert.Equal((8, true, "http_503"), (failed.Attempts, failed.Failed, failed.Error));
        Assert.False(await dispatcher.DispatchOneAsync(Token));
        await Subscribe(scope, "type", target: receiver.Target);
        var next = await Create(scope);
        await Finish(next);
        Assert.True(await dispatcher.DispatchOneAsync(Token));
        Assert.Equal(2, receiver.Deliveries.Count);
    }

    [Theory]
    [InlineData("type")]
    [InlineData("target")]
    [InlineData("secret")]
    public async Task Unapproved_type_destination_or_secret_never_receives_payload_or_uses_another_types_key(string mismatch)
    {
        await using var receiver = await Receiver.Start(Token);
        var scope = await Registry();
        await Subscribe(scope, target: receiver.Target);
        var run = await Create(scope);
        await Finish(run);
        using var options = Options(mismatch == "type" ? scope with { Type = "foreign/type" } : scope,
            mismatch == "target" ? receiver.Target + "/other" : receiver.Target,
            mismatch == "secret" ? "secret://notifications/foreign" : Secret);
        using var dispatcher = Dispatcher(options);
        Assert.True(await dispatcher.DispatchOneAsync(Token));
        Assert.True((await State(run.Snapshot.RunId)).Failed);
        Assert.Equal("destination_not_approved", (await State(run.Snapshot.RunId)).Error);
        Assert.Empty(receiver.Deliveries);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("extra")]
    [InlineData("foreign-id")]
    [InlineData("oversized")]
    public async Task Invalid_or_expanded_persisted_payload_is_failed_without_sending_private_data(string corruption)
    {
        await using var receiver = await Receiver.Start(Token);
        var scope = await Registry();
        await Subscribe(scope, target: receiver.Target);
        var run = await Create(scope);
        await Finish(run);
        var mutation = corruption switch
        {
            "empty" => "'{}'::jsonb",
            "extra" => """payload||'{"variables":{"private":"sensitive"}}'::jsonb""",
            "foreign-id" => "jsonb_set(payload,'{id}','\"00000000-0000-0000-0000-000000000001\"')",
            "oversized" => "payload||jsonb_build_object('private',repeat('x',17000))",
            _ => throw new ArgumentException("Unknown fixture corruption.")
        };
        await Execute($"UPDATE notification_outbox SET payload={mutation} WHERE run_id=$1", run.Snapshot.RunId);
        using var options = Options(scope, receiver.Target);
        using var dispatcher = Dispatcher(options);
        Assert.True(await dispatcher.DispatchOneAsync(Token));
        Assert.Equal("invalid_cloud_event", (await State(run.Snapshot.RunId)).Error);
        Assert.Empty(receiver.Deliveries);
    }

    [Fact]
    public async Task Concurrent_independent_dispatchers_lock_one_subscription_without_duplicate_sends()
    {
        await using var receiver = await Receiver.Start(Token);
        var scope = await Registry();
        await Subscribe(scope, target: receiver.Target);
        var run = await Create(scope);
        await Finish(run);
        using var options = Options(scope, receiver.Target);
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 12).Select(async _ =>
        {
            using var dispatcher = Dispatcher(options);
            return await dispatcher.DispatchOneAsync(Token);
        }));
        Assert.Equal(1, outcomes.Count(result => result));
        Assert.Single(receiver.Deliveries);
        Assert.Equal((1, true), ((await State(run.Snapshot.RunId)).Attempts, (await State(run.Snapshot.RunId)).Delivered));
    }

    [Fact]
    public async Task Unsubscribe_waits_for_the_bounded_inflight_send_then_cascades_without_deadlock_or_later_delivery()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var receiver = await Receiver.Start(Token, gate: gate);
        var scope = await Registry();
        var subscription = await Subscribe(scope, target: receiver.Target);
        var run = await Create(scope);
        await Finish(run);
        using var options = Options(scope, receiver.Target);
        using var dispatcher = Dispatcher(options);
        var sending = dispatcher.DispatchOneAsync(Token);
        await receiver.Next(Token);
        var deleting = Execute("DELETE FROM subscriptions WHERE id=$1", subscription);
        try
        {
            await Task.Delay(100, Token);
            Assert.False(deleting.IsCompleted);
        }
        finally { gate.TrySetResult(); }
        await sending.WaitAsync(TimeSpan.FromSeconds(10), Token);
        await deleting.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Empty(await Outbox(run.Snapshot.RunId));
        Assert.False(await dispatcher.DispatchOneAsync(Token));
        Assert.Single(receiver.Deliveries);
    }

    [Fact]
    public async Task Cancellation_rolls_back_the_claim_and_preserves_the_same_event_for_at_least_once_retry()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var receiver = await Receiver.Start(Token, gate: gate);
        var scope = await Registry();
        await Subscribe(scope, target: receiver.Target);
        var run = await Create(scope);
        await Finish(run);
        using var options = Options(scope, receiver.Target);
        using var dispatcher = Dispatcher(options);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var sending = dispatcher.DispatchOneAsync(cancel.Token);
        await receiver.Next(Token);
        await cancel.CancelAsync();
        try { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending); }
        finally { gate.TrySetResult(); }
        var persisted = await State(run.Snapshot.RunId);
        Assert.Equal((0, false, false), (persisted.Attempts, persisted.Delivered, persisted.Failed));
        Assert.True(await dispatcher.DispatchOneAsync(Token));
        Assert.Equal(2, receiver.Deliveries.Count);
        Assert.Equal(receiver.Deliveries.First().Body, receiver.Deliveries.Last().Body);
    }

    [Fact]
    public async Task Successful_remote_send_followed_by_database_failure_retries_the_same_id_and_bytes_after_restart()
    {
        await using var receiver = await Receiver.Start(Token);
        var scope = await Registry();
        await Subscribe(scope, target: receiver.Target);
        var run = await Create(scope);
        await Finish(run);
        var name = "reject_delivery_" + Guid.NewGuid().ToString("N");
        await Execute($"""
            CREATE FUNCTION {name}() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'injected delivery commit failure'; END $$;
            CREATE TRIGGER {name} BEFORE UPDATE ON notification_outbox FOR EACH ROW
                WHEN (NEW.run_id='{run.Snapshot.RunId}') EXECUTE FUNCTION {name}();
            """);
        using var options = Options(scope, receiver.Target);
        try
        {
            using var first = Dispatcher(options);
            await Assert.ThrowsAsync<PostgresException>(() => first.DispatchOneAsync(Token));
            Assert.Equal((0, false), ((await State(run.Snapshot.RunId)).Attempts, (await State(run.Snapshot.RunId)).Delivered));
        }
        finally { await Execute($"DROP TRIGGER {name} ON notification_outbox; DROP FUNCTION {name}()"); }
        using var restarted = Dispatcher(options);
        Assert.True(await restarted.DispatchOneAsync(Token));
        Assert.Equal(2, receiver.Deliveries.Count);
        Assert.Equal(receiver.Deliveries.First().Body, receiver.Deliveries.Last().Body);
        foreach (var receipt in receiver.Deliveries) VerifySignature(receipt);
    }

    [Fact]
    public async Task Real_connection_failure_remains_pending_with_sanitized_error_and_backoff()
    {
        string target;
        await using (var receiver = await Receiver.Start(Token)) target = receiver.Target;
        var scope = await Registry();
        await Subscribe(scope, target: target);
        var run = await Create(scope);
        await Finish(run);
        using var options = Options(scope, target);
        using var dispatcher = Dispatcher(options);
        Assert.True(await dispatcher.DispatchOneAsync(Token));
        var row = await State(run.Snapshot.RunId);
        Assert.Equal((1, false, false, "transport_unavailable"), (row.Attempts, row.Delivered, row.Failed, row.Error));
    }

    [Fact]
    public async Task An_untrusted_TLS_certificate_is_not_bypassed_even_for_an_operator_approved_loopback_destination()
    {
        await using var receiver = await Receiver.Start(Token, tls: true);
        var scope = await Registry();
        await Subscribe(scope, target: receiver.Target);
        var run = await Create(scope);
        await Finish(run);
        using var options = Options(scope, receiver.Target);
        using var dispatcher = Dispatcher(options);
        Assert.True(await dispatcher.DispatchOneAsync(Token));
        Assert.Equal("transport_unavailable", (await State(run.Snapshot.RunId)).Error);
        Assert.Empty(receiver.Deliveries);
    }

    [Fact]
    public async Task Real_stalled_response_times_out_with_a_bounded_durable_retry_instead_of_holding_the_claim_forever()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var receiver = await Receiver.Start(Token, gate: gate);
        var scope = await Registry();
        await Subscribe(scope, target: receiver.Target);
        var run = await Create(scope);
        await Finish(run);
        using var options = Options(scope, receiver.Target);
        using var dispatcher = Dispatcher(options);
        var watch = Stopwatch.StartNew();
        try { Assert.True(await dispatcher.DispatchOneAsync(Token).WaitAsync(TimeSpan.FromSeconds(15), Token)); }
        finally { gate.TrySetResult(); }
        Assert.InRange(watch.Elapsed.TotalSeconds, 9, 15);
        Assert.Equal("delivery_timeout", (await State(run.Snapshot.RunId)).Error);
        Assert.False((await State(run.Snapshot.RunId)).Failed);
        Assert.Single(receiver.Deliveries);
    }

    [Fact]
    public async Task IPv6_loopback_is_pinned_and_delivered_with_the_same_signed_protocol()
    {
        await using var receiver = await Receiver.Start(Token, ipv6: true);
        var scope = await Registry();
        await Subscribe(scope, target: receiver.Target);
        var run = await Create(scope);
        await Finish(run);
        using var options = Options(scope, receiver.Target);
        using var dispatcher = Dispatcher(options);
        Assert.True(await dispatcher.DispatchOneAsync(Token));
        Assert.True((await State(run.Snapshot.RunId)).Delivered);
        VerifySignature(Assert.Single(receiver.Deliveries));
    }

    [Fact]
    public async Task Upgrade_preserves_old_terminal_history_without_backfill_and_covers_new_future_partitions()
    {
        var schema = "notification_upgrade_" + Guid.NewGuid().ToString("N");
        await Execute($"CREATE SCHEMA {schema}");
        try
        {
            await using var previous = database.ForSchema(schema);
            foreach (var migration in Migrator.LoadScripts().Where(script => string.CompareOrdinal(script.Name, "0039") < 0))
            {
                await using var command = previous.CreateCommand(await File.ReadAllTextAsync(
                    Path.Combine(AppContext.BaseDirectory, "Migrations", migration.Name), Token));
                await command.ExecuteNonQueryAsync(Token);
            }
            await using (var seed = previous.CreateCommand("""
                WITH team AS (INSERT INTO teams(id,name) VALUES('notify','Notifications') RETURNING id),
                type AS (
                    INSERT INTO workflow_types(id,team_id,name,normalized_name,description,metadata_schema)
                    SELECT id||'/type',id,'type','type','Notifications','{}' FROM team RETURNING id
                )
                INSERT INTO subscriptions(id,principal_id,scope_kind,scope_id,type_scope,events,channel,target,secret_ref)
                SELECT gen_random_uuid(),gen_random_uuid(),'type',id,id,ARRAY['Completed'],'Webhook',
                    'https://notify.example.test/hook','secret://notifications/webhook' FROM type;
                """))
                await seed.ExecuteNonQueryAsync(Token);
            var old = Guid.CreateVersion7();
            await SeedTerminal(previous, old);
            await using (var migration = previous.CreateCommand(await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, "Migrations", "0039_terminal_notifications.sql"), Token)))
                await migration.ExecuteNonQueryAsync(Token);
            await using (var unchanged = previous.CreateCommand("UPDATE runs SET status=status,ended_at=ended_at"))
                await unchanged.ExecuteNonQueryAsync(Token);
            await using (var count = previous.CreateCommand("SELECT count(*) FROM notification_outbox"))
                Assert.Equal(0L, await count.ExecuteScalarAsync(Token));
            var future = Guid.CreateVersion7(DateTimeOffset.UtcNow.AddDays(40));
            await SeedTerminal(previous, future);
            await using var queued = previous.CreateCommand("SELECT run_id,payload->>'time' FROM notification_outbox");
            await using var reader = await queued.ExecuteReaderAsync(Token);
            Assert.True(await reader.ReadAsync(Token));
            Assert.Equal(future, reader.GetGuid(0));
            Assert.EndsWith("Z", reader.GetString(1));
            Assert.False(await reader.ReadAsync(Token));
        }
        finally { await Execute($"DROP SCHEMA {schema} CASCADE"); }
    }

    [Fact]
    public async Task Actual_Program_enables_the_configured_dispatcher_and_delivers_after_a_real_terminal_commit()
    {
        await using var receiver = await Receiver.Start(Token);
        var scope = await Registry();
        await Subscribe(scope, target: receiver.Target);
        var run = await Create(scope);
        using var enginePort = new TcpListener(IPAddress.Loopback, 0);
        using var apiPort = new TcpListener(IPAddress.Loopback, 0);
        enginePort.Start();
        apiPort.Start();
        var engine = ((IPEndPoint)enginePort.LocalEndpoint).Port;
        var api = ((IPEndPoint)apiPort.LocalEndpoint).Port;
        var root = Path.GetFullPath(Path.Combine(".agent-artifacts", "notification-program-" + Guid.NewGuid().ToString("N")));
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(typeof(Migrator).Assembly.Location);
        start.ArgumentList.Add("--dev");
        foreach (var (name, value) in new Dictionary<string, string>
        {
            ["DOTNET_ENVIRONMENT"] = "Development", ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["ConnectionStrings__Workflow"] = database.ConnectionString,
            ["Engine__Port"] = engine.ToString(CultureInfo.InvariantCulture),
            ["Api__Port"] = api.ToString(CultureInfo.InvariantCulture), ["Storage__Root"] = root,
            ["Storage__UploadHmacKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            ["Notifications__Enabled"] = "true", ["Notifications__Webhooks__0__TypeScope"] = scope.Type,
            ["Notifications__Webhooks__0__Target"] = receiver.Target,
            ["Notifications__Webhooks__0__SecretRef"] = Secret,
            ["Notifications__Webhooks__0__SigningKeyBase64"] = Convert.ToBase64String(Key),
            ["Notifications__Webhooks__0__Addresses__0"] = "127.0.0.1"
        }) start.Environment[name] = value;
        enginePort.Stop();
        apiPort.Stop();
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(Token);
        var stderr = process.StandardError.ReadToEndAsync(Token);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(45));
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3) };
            while (true)
            {
                if (process.HasExited) Assert.Fail($"Program exited {process.ExitCode}.\n{await stdout}\n{await stderr}");
                try
                {
                    using var ready = await client.GetAsync($"http://127.0.0.1:{api}/healthz", deadline.Token);
                    if (ready.IsSuccessStatusCode) break;
                }
                catch (HttpRequestException) { }
                await Task.Delay(50, deadline.Token);
            }
            var watch = Stopwatch.StartNew();
            await Finish(run);
            VerifySignature(await receiver.Next(deadline.Token));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30));
            while (!(await State(run.Snapshot.RunId)).Delivered)
                await Task.Delay(20, deadline.Token);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
            Assert.DoesNotContain(Convert.ToBase64String(Key), await stdout);
            Assert.DoesNotContain(Convert.ToBase64String(Key), await stderr);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private async Task SeedTerminal(NpgsqlDataSource source, Guid id)
    {
        await using (var partitions = source.CreateCommand("SELECT ensure_run_partitions(uuid_v7_run_day($1))"))
        {
            partitions.Parameters.AddWithValue(id);
            await partitions.ExecuteNonQueryAsync(Token);
        }
        await using var insert = source.CreateCommand("""
            INSERT INTO runs(run_day,id,workflow_id,version_id,trigger,triggered_by,variables,metadata,status,created_at,ended_at,deadline_at)
            VALUES(uuid_v7_run_day($1),$1,'notify/type/workflow',$1,0,'test','{}','{}',3,now(),now(),now()+interval '1 day')
            """);
        insert.Parameters.AddWithValue(id);
        await insert.ExecuteNonQueryAsync(Token);
    }

    private async Task<Scope> Registry()
    {
        var team = "notification-" + Guid.NewGuid().ToString("N");
        var scope = new Scope(team + "/type/workflow", team + "/type", Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.NewGuid());
        await using var connection = await database.DataSource.OpenConnectionAsync(Token);
        await using var transaction = await connection.BeginTransactionAsync(Token);
        await new RegistryWriter().UpsertAsync(connection, transaction, new(team,
            [new(team, "type", "Notification integration", new([]))],
            RoleBindings: [new(Contracts.Registry.RegistryRole.Viewer, scope.Group)]), Token);
        await using var command = new NpgsqlCommand("""
            WITH workflow AS (
                INSERT INTO workflows(id,type_id) VALUES($1,$2) RETURNING id
            ), schedule AS (
                INSERT INTO schedules(id,workflow_id,origin,name,timezone,interval_s,created_by,updated_by)
                SELECT $3,id,1,'Notify','UTC',60,'test','test' FROM workflow RETURNING id,workflow_id
            )
            INSERT INTO run_series(id,workflow_id,schedule_id) SELECT $4,workflow_id,id FROM schedule
            """, connection);
        foreach (var value in new object[] { scope.Workflow, scope.Type, scope.Schedule, scope.Series })
            command.Parameters.AddWithValue(value);
        await command.ExecuteNonQueryAsync(Token);
        await transaction.CommitAsync(Token);
        return scope;
    }

    private async Task<Guid> Subscribe(Scope scope, string kind = "workflow", string[]? events = null,
        string channel = "Webhook", string? target = null)
    {
        var id = Guid.CreateVersion7();
        await Execute("""
            INSERT INTO subscriptions(id,principal_id,scope_kind,scope_id,type_scope,series_id,events,channel,target,secret_ref)
            VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10)
            """, id, Guid.NewGuid(), kind, kind switch
            {
                "workflow" => scope.Workflow, "type" => scope.Type, "schedule" => scope.Schedule.ToString(),
                _ => throw new ArgumentException("Unknown fixture scope.")
            }, scope.Type,
            new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid, Value = kind == "schedule" ? scope.Series : DBNull.Value },
            events ?? ["Completed"], channel, channel switch
            {
                "Webhook" => target ?? "https://notify.example.test/hook",
                "Email" => "subscriber@example.test", "Teams" => "secret://notifications/teams",
                _ => throw new ArgumentException("Unknown fixture channel.")
            }, new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text, Value = channel == "Webhook" ? Secret : DBNull.Value });
        return id;
    }

    private async Task<RunState> Create(Scope scope)
    {
        var run = new RunState(Guid.CreateVersion7(), [new("alpha", [], [new("task")])],
            new WorkflowMetadata("private-metadata", ImmutableSortedDictionary<string, string>.Empty.Add("private", "never-send")),
            Assert.Fail);
        await store.CreateAsync(run, scope.Workflow, Guid.CreateVersion7(), DateTimeOffset.UtcNow.AddMinutes(5), Token);
        await Execute("UPDATE runs SET series_id=$2 WHERE id=$1", run.Snapshot.RunId, scope.Series);
        return run;
    }

    private async Task Finish(RunState run, RunStatus status = RunStatus.Succeeded, bool grouped = false)
    {
        var changes = Changes(run, status);
        if (grouped)
        {
            await using var writer = new GroupCommitAppender(database.DataSource, NullLogger<GroupCommitAppender>.Instance);
            await writer.AppendAsync(changes, Token);
        }
        else foreach (var change in changes) await store.AppendAsync(run, change, Token);
    }

    private static RunEvent[] Changes(RunState run, RunStatus status)
    {
        var copy = RunState.Restore(run.Snapshot, [], Assert.Fail);
        var at = DateTimeOffset.UtcNow;
        var result = new List<RunEvent> { copy.Append(EventEntity.Run, null, (int)RunStatus.Running, at) };
        foreach (var state in new[] { ExecutionStatus.Ready, ExecutionStatus.Dispatched, ExecutionStatus.Running })
            result.Add(copy.Append(EventEntity.Node, "alpha", (int)state, at));
        result.Add(copy.Append(EventEntity.Node, "alpha", (int)(status switch
        {
            RunStatus.Succeeded => ExecutionStatus.Succeeded, RunStatus.Aborted => ExecutionStatus.Cancelled,
            RunStatus.TimedOut => ExecutionStatus.TimedOut, _ => ExecutionStatus.Failed
        }), at.AddMilliseconds(10)));
        if (status == RunStatus.Aborted)
            result.Add(copy.Append(EventEntity.Run, null, (int)RunStatus.Aborting, at.AddMilliseconds(10)));
        result.Add(copy.Append(EventEntity.Run, null, (int)status, at.AddMilliseconds(20)));
        return result.ToArray();
    }

    private NotificationDispatcher Dispatcher(NotificationOptions options, TimeProvider? clock = null) =>
        new(database.DataSource, options, clock ?? TimeProvider.System, NullLogger<NotificationDispatcher>.Instance);

    private static NotificationOptions Options(Scope scope, string target, string secret = Secret) =>
        NotificationOptions.Read(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Notifications:Enabled"] = "true", ["Notifications:Webhooks:0:TypeScope"] = scope.Type,
            ["Notifications:Webhooks:0:Target"] = target, ["Notifications:Webhooks:0:SecretRef"] = secret,
            ["Notifications:Webhooks:0:SigningKeyBase64"] = Convert.ToBase64String(Key),
            ["Notifications:Webhooks:0:Addresses:0"] = new Uri(target).HostNameType == UriHostNameType.IPv6 ? "::1" : "127.0.0.1"
        }).Build());

    private sealed record Queued(Guid Subscription, string Payload);
    private async Task<List<Queued>> Outbox(Guid run)
    {
        await using var command = database.DataSource.CreateCommand(
            "SELECT subscription_id,payload::text FROM notification_outbox WHERE run_id=$1");
        command.Parameters.AddWithValue(run);
        await using var reader = await command.ExecuteReaderAsync(Token);
        var rows = new List<Queued>();
        while (await reader.ReadAsync(Token)) rows.Add(new(reader.GetGuid(0), reader.GetString(1)));
        return rows;
    }

    private sealed record DeliveryState(int Attempts, bool Delivered, bool Failed, string? Error, DateTimeOffset Next);
    private async Task<DeliveryState> State(Guid run)
    {
        await using var command = database.DataSource.CreateCommand("""
            SELECT attempts,delivered_at IS NOT NULL,failed_at IS NOT NULL,last_error,next_attempt_at
            FROM notification_outbox WHERE run_id=$1
            """);
        command.Parameters.AddWithValue(run);
        await using var reader = await command.ExecuteReaderAsync(Token);
        Assert.True(await reader.ReadAsync(Token));
        var row = new DeliveryState(reader.GetInt32(0), reader.GetBoolean(1), reader.GetBoolean(2),
            reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(4));
        Assert.False(await reader.ReadAsync(Token));
        return row;
    }

    private async Task Execute(string sql, params object[] values)
    {
        await using var command = database.DataSource.CreateCommand(sql);
        foreach (var value in values)
            if (value is NpgsqlParameter parameter) command.Parameters.Add(parameter);
            else command.Parameters.AddWithValue(value);
        await command.ExecuteNonQueryAsync(Token);
    }

    private async Task<long> Scalar(string sql, params object[] values)
    {
        await using var command = database.DataSource.CreateCommand(sql);
        foreach (var value in values) command.Parameters.AddWithValue(value);
        return (long)(await command.ExecuteScalarAsync(Token))!;
    }

    private static void VerifySignature(Receipt receipt)
    {
        Assert.Equal("application/cloudevents+json", receipt.ContentType);
        Assert.True(long.TryParse(receipt.Timestamp, out var seconds));
        Assert.InRange(Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - seconds), 0, 300);
        var prefix = Encoding.ASCII.GetBytes(receipt.Timestamp + ".");
        byte[] message = [.. prefix, .. receipt.Body];
        var expected = HMACSHA256.HashData(Key, message);
        Assert.StartsWith("sha256=", receipt.Signature);
        Assert.True(CryptographicOperations.FixedTimeEquals(expected, Convert.FromHexString(receipt.Signature[7..])));
        Assert.Empty(receipt.Cookie);
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = new(DateTimeOffset.UtcNow.Ticks / 10 * 10, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }

    private sealed record Receipt(byte[] Body, string Timestamp, string Signature, string ContentType, string Cookie);
    private sealed class Receiver(WebApplication app, string target, Channel<Receipt> received, TaskCompletionSource? gate,
        X509Certificate2? certificate) : IAsyncDisposable
    {
        public string Target { get; } = target;
        public ConcurrentQueue<Receipt> Deliveries { get; } = new();
        public async Task<Receipt> Next(CancellationToken token) =>
            await received.Reader.ReadAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(25), token);

        public static async Task<Receiver> Start(CancellationToken token, Func<int, int>? status = null,
            string? redirect = null, TaskCompletionSource? gate = null, bool tls = false, bool ipv6 = false)
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            X509Certificate2? certificate = null;
            if (tls)
            {
                using var rsa = RSA.Create(2048);
                var request = new CertificateRequest("CN=notification-untrusted", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(5));
                builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listener => listener.UseHttps(certificate)));
            }
            else builder.WebHost.UseUrls(ipv6 ? "http://[::1]:0" : "http://127.0.0.1:0");
            builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 16 * 1024);
            var app = builder.Build();
            var received = Channel.CreateUnbounded<Receipt>();
            Receiver? receiver = null;
            var attempts = 0;
            app.MapPost("/hook", async context =>
            {
                using var body = new MemoryStream();
                await context.Request.Body.CopyToAsync(body, context.RequestAborted);
                var receipt = new Receipt(body.ToArray(), context.Request.Headers[CloudEventSigner.TimestampHeader].ToString(),
                    context.Request.Headers[CloudEventSigner.SignatureHeader].ToString(), context.Request.ContentType ?? "",
                    context.Request.Headers.Cookie.ToString());
                receiver!.Deliveries.Enqueue(receipt);
                await received.Writer.WriteAsync(receipt, context.RequestAborted);
                if (gate is not null) await gate.Task;
                context.Response.Headers.SetCookie = "private=must-not-resend";
                if (redirect is not null) context.Response.Headers.Location = redirect;
                context.Response.StatusCode = status?.Invoke(Interlocked.Increment(ref attempts)) ?? 204;
            });
            await app.StartAsync(token);
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            receiver = new Receiver(app, address + "/hook", received, gate, certificate);
            return receiver;
        }

        public async ValueTask DisposeAsync()
        {
            gate?.TrySetResult();
            received.Writer.TryComplete();
            await app.StopAsync();
            await app.DisposeAsync();
            certificate?.Dispose();
        }
    }
}
