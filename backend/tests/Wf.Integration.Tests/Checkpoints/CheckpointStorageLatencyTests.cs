using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Wf.ControlPlane.Engine.Checkpoints;
using Wf.ControlPlane.Storage;

namespace Wf.Integration.Tests.Checkpoints;

public sealed partial class CheckpointApplierTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Blob_IO_releases_assignment_and_allocation_locks_and_revalidates_the_session_before_success(
        bool upload, bool reconnect)
    {
        await using var fixture = await Fixture.Create(database);
        var saved = await fixture.Upload("{}");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blobs = new PausedBlobs(fixture.Blobs, entered, release);
        var applier = new CheckpointApplier(database.DataSource, fixture.Runtime.Store, blobs,
            NullLogger<CheckpointApplier>.Instance);
        using var body = new MemoryStream("{}"u8.ToArray());
        var work = upload
            ? applier.UploadAsync(fixture.Runtime.Session, saved, body, Token)
            : applier.AcceptAsync(fixture.Runtime.Session, saved, Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            await using var connection = await database.DataSource.OpenConnectionAsync(Token);
            await using var transaction = await connection.BeginTransactionAsync(Token);
            await using var allocation = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock(746312081)", connection);
            Assert.Equal(true, await allocation.ExecuteScalarAsync(Token));
            await using var assignment = new NpgsqlCommand("""
                SELECT 1 FROM agents g JOIN task_attempts a ON a.agent_id=g.id
                JOIN runs r ON r.run_day=a.run_day AND r.id=a.run_id
                WHERE g.id=$1 AND r.id=$2 FOR UPDATE OF g,r,a NOWAIT
                """, connection);
            assignment.Parameters.AddWithValue(fixture.Runtime.Session.AgentId);
            assignment.Parameters.AddWithValue(fixture.Runtime.RunId);
            await assignment.ExecuteNonQueryAsync(Token);
            if (reconnect)
            {
                await using var supersede = new NpgsqlCommand("UPDATE agents SET session_id=$2 WHERE id=$1", connection);
                supersede.Parameters.AddWithValue(fixture.Runtime.Session.AgentId);
                supersede.Parameters.AddWithValue(Guid.NewGuid());
                await supersede.ExecuteNonQueryAsync(Token);
            }
            await transaction.CommitAsync(Token);
        }
        finally { release.TrySetResult(); }
        if (reconnect)
        {
            if (upload) await Assert.ThrowsAsync<UnauthorizedAccessException>(() => work);
            else await Assert.ThrowsAsync<ArgumentException>(() => work);
        }
        else await work;
        Assert.Equal(!upload && !reconnect ? 1L : 0L,
            await fixture.Runtime.Scalar("SELECT count(*) FROM task_checkpoints WHERE run_id=$1"));
    }

    private sealed class PausedBlobs(IBlobStore inner, TaskCompletionSource entered, TaskCompletionSource release) : IBlobStore
    {
        public async Task UploadAsync(BlobContent content, Stream source, CancellationToken cancellationToken = default)
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            await inner.UploadAsync(content, source, cancellationToken);
        }

        public async Task DownloadAsync(BlobContent content, Stream destination, CancellationToken cancellationToken = default)
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            await inner.DownloadAsync(content, destination, cancellationToken);
        }
    }
}
