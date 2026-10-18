using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Recovery;
using Wf.ControlPlane.Engine.Registry;
using Wf.Engine.Core;

namespace Wf.Integration.Tests.Registration;

public sealed partial class RegistrationTests
{
    [Fact]
    public async Task ActiveVersion_new_registration_becomes_available_without_restart()
    {
        await using var server = await Server.Start(database.DataSource);
        var request = await Request(server);
        using var cache = Versions();
        await cache.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await WaitFor(() => cache.IsReady);
            Assert.Throws<KeyNotFoundException>(() => cache.GetActive(request.WorkflowId!));
            var elapsed = Stopwatch.StartNew();
            using var response = await server.Post(request);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var version = (await Json(response)).GetProperty("workflowVersion").GetGuid();
            await WaitFor(() => HasVersion(cache, request.WorkflowId!, version));
            Assert.True(elapsed.Elapsed <= TimeSpan.FromSeconds(5), $"Activation took {elapsed.Elapsed}.");
            var active = cache.GetActive(request.WorkflowId!);
            Assert.Equal("1.0.0", active.Manifest.Version);
            Assert.Equal(request.WorkflowId, active.Manifest.Name);
            Assert.Equal(request.Package!.Sha256, active.Package.Sha256);
            Assert.Equal(request.Package.Size, active.Package.SizeBytes);
        }
        finally { await cache.StopAsync(TestContext.Current.CancellationToken); }
        Assert.False(cache.IsReady);
        Assert.Throws<InvalidOperationException>(() => cache.GetActive(request.WorkflowId!));
    }

    [Fact]
    public async Task ActiveVersion_startup_loads_existing_rows_and_selected_command_and_run_versions_remain_pinned()
    {
        await using var server = await Server.Start(database.DataSource);
        var request = await Request(server);
        using var registered = await server.Post(request);
        var original = (await Json(registered)).GetProperty("workflowVersion").GetGuid();
        using var cache = Versions();
        await cache.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await WaitFor(() => HasVersion(cache, request.WorkflowId!, original));
            var pinned = cache.GetActive(request.WorkflowId!);
            var oldRun = Initial(pinned);
            var inbox = new CommandInbox(database.DataSource, TimeProvider.System);
            var deadline = DateTimeOffset.UtcNow.AddHours(1);
            var command = await inbox.TriggerAsync(new(oldRun.Snapshot, request.WorkflowId!, pinned.VersionId, deadline),
                "active-version-test", TestContext.Current.CancellationToken);
            using var update = await server.Post(request with { Manifest = Manifest(request.WorkflowId!, "1.0.1") });
            Assert.Equal(HttpStatusCode.Created, update.StatusCode);
            var nextId = (await Json(update)).GetProperty("workflowVersion").GetGuid();
            await WaitFor(() => HasVersion(cache, request.WorkflowId!, nextId));
            var next = cache.GetActive(request.WorkflowId!);
            Assert.NotSame(pinned, next);
            Assert.Equal(original, pinned.VersionId);
            Assert.Equal("1.0.0", pinned.Manifest.Version);
            Assert.Equal(original.ToString(), await Value("SELECT payload->>'VersionId' FROM commands WHERE id=$1", command));
            var store = new RunStore(database.DataSource, NullLogger<RunStore>.Instance);
            await store.CreateAsync(oldRun, request.WorkflowId!, pinned.VersionId, deadline, TestContext.Current.CancellationToken);
            var newRun = Initial(next);
            await store.CreateAsync(newRun, request.WorkflowId!, next.VersionId, deadline, TestContext.Current.CancellationToken);
            Assert.Equal(original, await Value("SELECT version_id FROM runs WHERE id=$1", oldRun.Snapshot.RunId));
            Assert.Equal(nextId, await Value("SELECT version_id FROM runs WHERE id=$1", newRun.Snapshot.RunId));
            var stored = await store.LoadAsync(oldRun.Snapshot.RunId, TestContext.Current.CancellationToken);
            Assert.Equal(oldRun.Snapshot.Metadata.Type, stored.Snapshot.Metadata.Type);
            Assert.Equal(oldRun.Snapshot.Metadata.Values.ToArray(), stored.Snapshot.Metadata.Values.ToArray());
        }
        finally { await cache.StopAsync(TestContext.Current.CancellationToken); }
    }

    [Fact]
    public async Task ActiveVersion_notify_is_commit_scoped_and_manual_rollback_activates_the_older_version_only_after_commit()
    {
        await using var server = await Server.Start(database.DataSource);
        var request = await Request(server);
        using var first = await server.Post(request);
        var original = (await Json(first)).GetProperty("workflowVersion").GetGuid();
        using var update = await server.Post(request with { Manifest = Manifest(request.WorkflowId!, "1.0.1") });
        var latest = (await Json(update)).GetProperty("workflowVersion").GetGuid();
        using var cache = Versions();
        await cache.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await WaitFor(() => HasVersion(cache, request.WorkflowId!, latest));
            await using var connection = await database.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
            await using (var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken))
            {
                await PointerAndNotify(connection, transaction, request.WorkflowId!, original);
                Assert.Equal(latest, cache.GetActive(request.WorkflowId!).VersionId);
                await transaction.RollbackAsync(TestContext.Current.CancellationToken);
            }
            Assert.Equal(latest, cache.GetActive(request.WorkflowId!).VersionId);
            await using (var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken))
            {
                await PointerAndNotify(connection, transaction, request.WorkflowId!, original);
                Assert.Equal(latest, cache.GetActive(request.WorkflowId!).VersionId);
                await transaction.CommitAsync(TestContext.Current.CancellationToken);
            }
            await WaitFor(() => HasVersion(cache, request.WorkflowId!, original));
            Assert.Equal(latest, await Value("SELECT latest_version_id FROM workflows WHERE id=$1", request.WorkflowId!));
        }
        finally { await cache.StopAsync(TestContext.Current.CancellationToken); }
    }

    [Theory]
    [InlineData("deleted_at=now()")]
    [InlineData("active_version_id=NULL")]
    public async Task ActiveVersion_inactive_or_deleted_workflows_leave_the_cache_without_invalidating_existing_pins(string mutation)
    {
        await using var server = await Server.Start(database.DataSource);
        var request = await Request(server);
        using var registered = await server.Post(request);
        var original = (await Json(registered)).GetProperty("workflowVersion").GetGuid();
        using var cache = Versions();
        await cache.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await WaitFor(() => HasVersion(cache, request.WorkflowId!, original));
            var pinned = cache.GetActive(request.WorkflowId!);
            await using var connection = await database.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
            await using (var delete = new NpgsqlCommand($"UPDATE workflows SET {mutation} WHERE id=$1", connection, transaction))
            {
                delete.Parameters.AddWithValue(request.WorkflowId!);
                await delete.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            await Notify(connection, transaction, request.WorkflowId!);
            await transaction.CommitAsync(TestContext.Current.CancellationToken);
            await WaitFor(() => cache.IsReady && !HasVersion(cache, request.WorkflowId!, original));
            Assert.Throws<KeyNotFoundException>(() => cache.GetActive(request.WorkflowId!));
            Assert.Equal(original, pinned.VersionId);
            Assert.Equal("1.0.0", pinned.Manifest.Version);
        }
        finally { await cache.StopAsync(TestContext.Current.CancellationToken); }
    }

    [Fact]
    public async Task ActiveVersion_both_instances_refresh_and_schedule_only_changes_keep_the_same_workflow_version()
    {
        await using var server = await Server.Start(database.DataSource);
        var request = await Request(server);
        using var registered = await server.Post(request);
        var original = (await Json(registered)).GetProperty("workflowVersion").GetGuid();
        using var first = Versions();
        using var second = Versions();
        await first.StartAsync(TestContext.Current.CancellationToken);
        await second.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await WaitFor(() => HasVersion(first, request.WorkflowId!, original) && HasVersion(second, request.WorkflowId!, original));
            using var schedule = await server.Post(ScheduleRequest(request, ScheduleFile()));
            Assert.Equal(HttpStatusCode.Created, schedule.StatusCode);
            using var changed = await server.Post(request with { Manifest = Manifest(request.WorkflowId!, "1.0.1") });
            var latest = (await Json(changed)).GetProperty("workflowVersion").GetGuid();
            await WaitFor(() => HasVersion(first, request.WorkflowId!, latest) && HasVersion(second, request.WorkflowId!, latest));
            Assert.Equal("1.0.1", first.GetActive(request.WorkflowId!).Manifest.Version);
            Assert.Equal("1.0.1", second.GetActive(request.WorkflowId!).Manifest.Version);
            Assert.NotEqual(original, latest);
        }
        finally
        {
            await first.StopAsync(TestContext.Current.CancellationToken);
            await second.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task ActiveVersion_listener_loss_fails_closed_logs_and_resynchronizes_a_missed_commit_after_reconnect()
    {
        await using var server = await Server.Start(database.DataSource);
        var request = await Request(server);
        using var registered = await server.Post(request);
        var original = (await Json(registered)).GetProperty("workflowVersion").GetGuid();
        var name = "wf-cache-" + Guid.NewGuid().ToString("N");
        await using var source = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            ApplicationName = name
        }.ConnectionString);
        var logger = new CacheLogger();
        using var cache = new ActiveVersionCache(source, logger, TimeProvider.System);
        await cache.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await WaitFor(() => HasVersion(cache, request.WorkflowId!, original));
            var pid = Assert.IsType<int>(await Value("""
                SELECT pid FROM pg_stat_activity WHERE application_name=$1 AND query='LISTEN registry'
                """, name));
            Assert.Equal(true, await Value("SELECT pg_terminate_backend($1)", pid));
            await WaitFor(() => !cache.IsReady && !logger.Errors.IsEmpty || cache.ExecuteTask!.IsCompleted);
            Assert.False(cache.ExecuteTask!.IsCompleted, cache.ExecuteTask.Exception?.ToString());
            Assert.Throws<InvalidOperationException>(() => cache.GetActive(request.WorkflowId!));
            using var changed = await server.Post(request with { Manifest = Manifest(request.WorkflowId!, "1.0.1") });
            Assert.Equal(HttpStatusCode.Created, changed.StatusCode);
            var latest = (await Json(changed)).GetProperty("workflowVersion").GetGuid();
            await WaitFor(() => HasVersion(cache, request.WorkflowId!, latest));
            Assert.NotEmpty(logger.Errors);
            Assert.False(cache.ExecuteTask!.IsCompleted);
        }
        finally { await cache.StopAsync(TestContext.Current.CancellationToken); }
    }

    [Fact]
    public async Task ActiveVersion_corrupt_manifest_fails_explicitly_and_never_exposes_a_partial_or_stale_snapshot()
    {
        await using var server = await Server.Start(database.DataSource);
        var request = await Request(server);
        using var registered = await server.Post(request);
        var original = (await Json(registered)).GetProperty("workflowVersion").GetGuid();
        using var cache = Versions();
        await cache.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await WaitFor(() => HasVersion(cache, request.WorkflowId!, original));
            await Execute("UPDATE workflow_versions SET manifest=jsonb_set(manifest,'{name}','\"wrong/type/identity\"') WHERE id=$1", original);
            await using var connection = await database.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
            await Notify(connection, transaction, request.WorkflowId!);
            await transaction.CommitAsync(TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<InvalidDataException>(() => cache.ExecuteTask!.WaitAsync(
                TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            Assert.False(cache.IsReady);
            Assert.Throws<InvalidOperationException>(() => cache.GetActive(request.WorkflowId!));
        }
        finally
        {
            await Execute("UPDATE workflow_versions SET manifest=jsonb_set(manifest,'{name}',to_jsonb($2::text)) WHERE id=$1",
                original, request.WorkflowId!);
            await cache.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    private ActiveVersionCache Versions() =>
        new(database.DataSource, NullLogger<ActiveVersionCache>.Instance, TimeProvider.System);

    private static RunState Initial(ActiveWorkflowVersion version) =>
        new(Guid.CreateVersion7(), version.Manifest.Nodes, version.Manifest.Metadata, _ => { });

    private static bool HasVersion(ActiveVersionCache cache, string workflowId, Guid version)
    {
        if (!cache.IsReady) return false;
        try { return cache.GetActive(workflowId).VersionId == version; }
        catch (KeyNotFoundException) { return false; }
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private static async Task PointerAndNotify(NpgsqlConnection connection, NpgsqlTransaction transaction, string workflowId, Guid version)
    {
        await using var command = new NpgsqlCommand("UPDATE workflows SET active_version_id=$2 WHERE id=$1", connection, transaction);
        command.Parameters.AddWithValue(workflowId);
        command.Parameters.AddWithValue(version);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        await Notify(connection, transaction, workflowId);
    }

    private static async Task Notify(NpgsqlConnection connection, NpgsqlTransaction transaction, string workflowId)
    {
        await using var command = new NpgsqlCommand("SELECT pg_notify('registry',$1)", connection, transaction);
        command.Parameters.AddWithValue(workflowId);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private sealed class CacheLogger : ILogger<ActiveVersionCache>
    {
        public ConcurrentQueue<Exception> Errors { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error && exception is not null) Errors.Enqueue(exception);
        }
    }
}

public sealed class ActiveVersionStartupTests(RegistrationDatabase database) : IClassFixture<RegistrationDatabase>
{
    [Fact]
    public async Task ActiveVersion_empty_database_is_ready_and_unknown_workflow_is_not_a_success_shaped_default()
    {
        await using var count = database.DataSource.CreateCommand("SELECT count(*) FROM workflows");
        Assert.Equal(0L, await count.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        using var cache = new ActiveVersionCache(database.DataSource, NullLogger<ActiveVersionCache>.Instance, TimeProvider.System);
        await cache.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            while (!cache.IsReady) await Task.Delay(10, timeout.Token);
            Assert.Throws<KeyNotFoundException>(() => cache.GetActive("team/type/missing"));
        }
        finally { await cache.StopAsync(TestContext.Current.CancellationToken); }
    }
}
