using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using Wf.Contracts.Manifest;
using Wf.ControlPlane.Storage;

namespace Wf.ControlPlane.Engine.Registry;

public sealed record ActiveWorkflowVersion(Guid VersionId, WorkflowManifest Manifest, BlobContent Package);

/// <summary>Atomic active snapshots; callers retain the selected version when creating a run command.</summary>
public sealed class ActiveVersionCache(NpgsqlDataSource dataSource, ILogger<ActiveVersionCache> logger,
    TimeProvider time) : BackgroundService
{
    private static readonly State Unavailable = new(false, ImmutableDictionary<string, ActiveWorkflowVersion>.Empty);
    private State state = Unavailable;

    public bool IsReady => Volatile.Read(ref state).Ready;

    public ActiveWorkflowVersion GetActive(string workflowId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);
        var snapshot = Volatile.Read(ref state);
        if (!snapshot.Ready)
            throw new InvalidOperationException("Active workflow versions are not synchronized with durable storage.");
        return snapshot.Versions.TryGetValue(workflowId, out var version) ? version
            : throw new KeyNotFoundException($"Workflow '{workflowId}' has no active version.");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await using var listener = await dataSource.OpenConnectionAsync(stoppingToken);
                    var invalidated = false;
                    listener.Notification += (_, notification) =>
                    {
                        if (notification.Channel == "registry") invalidated = true;
                    };
                    await using (var listen = new NpgsqlCommand("LISTEN registry", listener))
                        await listen.ExecuteNonQueryAsync(stoppingToken);
                    // Listen before reading: commits racing the initial/reconnect snapshot cannot be lost.
                    await ReloadAsync(stoppingToken);
                    while (!stoppingToken.IsCancellationRequested)
                    {
                        if (invalidated)
                        {
                            invalidated = false;
                            await ReloadAsync(stoppingToken);
                        }
                        if (!await listener.WaitAsync(5000, stoppingToken))
                        {
                            await using var heartbeat = new NpgsqlCommand("SELECT 1", listener);
                            await heartbeat.ExecuteScalarAsync(stoppingToken);
                        }
                    }
                }
                catch (Exception exception) when (exception is not InvalidDataException &&
                    exception is NpgsqlException { IsTransient: true } or IOException or TimeoutException)
                {
                    Volatile.Write(ref state, Unavailable);
                    logger.LogError(exception, "Active workflow versions are unavailable; reconnecting and resynchronizing.");
                    await Task.Delay(TimeSpan.FromSeconds(1), time, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { Volatile.Write(ref state, Unavailable); }
    }

    private async Task ReloadAsync(CancellationToken token)
    {
        var versions = ImmutableDictionary.CreateBuilder<string, ActiveWorkflowVersion>(StringComparer.Ordinal);
        await using var query = dataSource.CreateCommand("""
            SELECT w.id,v.id,v.semver,v.manifest::text,v.package_sha256,v.package_size
            FROM workflows w JOIN workflow_versions v ON v.workflow_id=w.id AND v.id=w.active_version_id
            WHERE w.deleted_at IS NULL
            """);
        await using var reader = await query.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var manifest = JsonSerializer.Deserialize(reader.GetString(3), WorkflowManifestJsonContext.Default.WorkflowManifest)
                ?? throw new InvalidDataException("An active workflow manifest is null.");
            var workflowId = reader.GetString(0);
            if (manifest.Name != workflowId || manifest.Version != reader.GetString(2))
                throw new InvalidDataException("An active workflow manifest does not match its stored identity.");
            var package = new BlobContent(BlobKind.Packages, Convert.ToHexStringLower(reader.GetFieldValue<byte[]>(4)), reader.GetInt64(5));
            versions.Add(workflowId, new(reader.GetGuid(1), manifest, package));
        }
        token.ThrowIfCancellationRequested();
        Volatile.Write(ref state, new(true, versions.ToImmutable()));
        logger.LogInformation("Synchronized {WorkflowCount} active workflow versions.", versions.Count);
    }

    private sealed record State(bool Ready, ImmutableDictionary<string, ActiveWorkflowVersion> Versions);
}
