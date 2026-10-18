using Azure;
using Npgsql;
using Wf.ControlPlane.Storage;

namespace Wf.ControlPlane.Engine;

public sealed class ResourceRetentionService(NpgsqlDataSource source, IBlobStore blobs,
    ILogger<ResourceRetentionService> logger) : BackgroundService
{
    public async Task SweepAsync(CancellationToken token)
    {
        await foreach (var run in blobs.RunObjectsAsync(token))
        {
            await using var lookup = source.CreateCommand("SELECT EXISTS(SELECT 1 FROM runs WHERE run_day=uuid_v7_run_day($1) AND id=$1)");
            lookup.Parameters.AddWithValue(run);
            if (await lookup.ExecuteScalarAsync(token) is false)
                await blobs.DeleteRunObjectsAsync(run, token);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await SweepAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) when (error is NpgsqlException or IOException or RequestFailedException or NotSupportedException)
            { logger.LogWarning(error, "Orphan run-object retention will retry."); }
            try { if (!await timer.WaitForNextTickAsync(stoppingToken)) break; }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
