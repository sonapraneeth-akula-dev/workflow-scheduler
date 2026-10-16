using Npgsql;
using Wf.ControlPlane.Hosting;

namespace Wf.ControlPlane.Engine.Leadership;

public sealed record LeaseToken(string Holder, long Epoch);
public sealed record LeaseOptions(string Holder, Uri Endpoint);

public sealed class NotLeaderException(string leaderHint) : InvalidOperationException("The engine does not hold the leader lease.")
{
    public string LeaderHint { get; } = leaderHint;
}

/// <summary>Database-time lease authority; local monotonic expiry only makes availability more conservative.</summary>
public sealed class LeaseManager : BackgroundService
{
    public static readonly TimeSpan RenewalPeriod = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan TimeToLive = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan AcquisitionPeriod = TimeSpan.FromSeconds(1);
    private readonly NpgsqlDataSource dataSource;
    private readonly LeaseOptions options;
    private readonly TimeProvider time;
    private readonly ILogger<LeaseManager> logger;
    private readonly SemaphoreSlim changes = new(1, 1);
    private State state = new(null, 0, TimeSpan.Zero, "");
    private int stopping;

    public LeaseManager(NpgsqlDataSource dataSource, LeaseOptions options, ILogger<LeaseManager> logger,
        TimeProvider? timeProvider = null, LocalComposeOptions? compose = null)
    {
        this.dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        time = timeProvider ?? TimeProvider.System;
        if (string.IsNullOrWhiteSpace(options.Holder) || options.Holder.Length > 200)
            throw new ArgumentException("Lease holder must be a nonempty instance identifier of at most 200 characters.", nameof(options));
        var endpoint = options.Endpoint;
        if (endpoint is null || !endpoint.IsAbsoluteUri || endpoint.Scheme is not ("http" or "https") ||
            endpoint.Scheme == "http" && !endpoint.IsLoopback &&
                !(compose is not null && LocalComposeOptions.IsEngineOrigin(endpoint)) || endpoint.UserInfo.Length != 0 ||
            endpoint.AbsolutePath != "/" || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
            throw new ArgumentException("Leader endpoint must be an HTTPS origin, or loopback HTTP origin, except for the explicit local Compose engine origins.", nameof(options));
    }

    public LeaseToken? Current
    {
        get
        {
            var observed = Volatile.Read(ref state);
            return observed.Lease is not null && time.GetElapsedTime(observed.ObservedAt) < observed.Remaining
                ? observed.Lease : null;
        }
    }

    public bool IsLeader => Current is not null;
    public string LeaderHint => Volatile.Read(ref state).LeaderHint;
    public LeaseToken RequireLease() => Current ?? throw new NotLeaderException(LeaderHint);

    public async Task<bool> TryAcquireOrRenewAsync(CancellationToken cancellationToken = default)
    {
        await changes.WaitAsync(cancellationToken);
        try
        {
            if (Volatile.Read(ref stopping) != 0) return false;
            var observedAt = time.GetTimestamp();
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using (var command = new NpgsqlCommand("""
                INSERT INTO leases(name,holder,leader_hint,epoch,expires_at)
                VALUES('engine',$1,$2,1,clock_timestamp()+interval '10 seconds')
                ON CONFLICT(name) DO UPDATE SET holder=EXCLUDED.holder,leader_hint=EXCLUDED.leader_hint,
                    epoch=CASE WHEN leases.holder=EXCLUDED.holder AND leases.expires_at>clock_timestamp()
                        THEN leases.epoch ELSE leases.epoch+1 END,
                    expires_at=clock_timestamp()+interval '10 seconds'
                WHERE leases.holder=EXCLUDED.holder OR leases.expires_at<=clock_timestamp()
                RETURNING epoch,expires_at-clock_timestamp()
                """, connection))
            {
                command.Parameters.AddWithValue(options.Holder);
                command.Parameters.AddWithValue(options.Endpoint.AbsoluteUri);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    var lease = new LeaseToken(options.Holder, reader.GetInt64(0));
                    var previous = Volatile.Read(ref state).Lease;
                    Volatile.Write(ref state, new(lease, observedAt, reader.GetTimeSpan(1), options.Endpoint.AbsoluteUri));
                    if (previous != lease) logger.LogInformation("Engine acquired leader epoch {Epoch}.", lease.Epoch);
                    return true;
                }
            }
            await using var hint = new NpgsqlCommand("""
                SELECT leader_hint,expires_at-clock_timestamp()
                FROM leases WHERE name='engine' AND expires_at>clock_timestamp()
                """, connection);
            await using var hintReader = await hint.ExecuteReaderAsync(cancellationToken);
            Volatile.Write(ref state, await hintReader.ReadAsync(cancellationToken)
                ? new(null, observedAt, hintReader.GetTimeSpan(1), hintReader.GetString(0))
                : new(null, observedAt, TimeSpan.Zero, ""));
            return false;
        }
        finally { changes.Release(); }
    }

    /// <summary>Call inside every engine write transaction using the token captured before asynchronous work.</summary>
    public async Task FenceAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, LeaseToken lease,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(lease);
        if (transaction.Connection != connection)
            throw new ArgumentException("The fence requires the write transaction's connection.", nameof(transaction));
        await using var command = new NpgsqlCommand("SELECT fence_engine_lease($1,$2)", connection, transaction);
        command.Parameters.AddWithValue(lease.Holder);
        command.Parameters.AddWithValue(lease.Epoch);
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == "WF001")
        {
            var observed = Volatile.Read(ref state);
            if (observed.Lease == lease)
                Interlocked.CompareExchange(ref state, new(null, 0, TimeSpan.Zero, ""), observed);
            logger.LogWarning("Rejected engine write from fenced epoch {Epoch}.", lease.Epoch);
            throw new NotLeaderException(LeaderHint);
        }
    }

    public async Task ReleaseAsync(CancellationToken cancellationToken = default)
    {
        await changes.WaitAsync(cancellationToken);
        try
        {
            var previous = Interlocked.Exchange(ref state, new(null, 0, TimeSpan.Zero, ""));
            if (previous.Lease is null) return;
            await using var command = dataSource.CreateCommand("""
                UPDATE leases SET expires_at=clock_timestamp()
                WHERE name='engine' AND holder=$1 AND epoch=$2
                """);
            command.Parameters.AddWithValue(previous.Lease.Holder);
            command.Parameters.AddWithValue(previous.Lease.Epoch);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally { changes.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var delay = RenewalPeriod;
                try
                {
                    if (!await TryAcquireOrRenewAsync(stoppingToken))
                    {
                        var observed = Volatile.Read(ref state);
                        var remaining = observed.Remaining - time.GetElapsedTime(observed.ObservedAt);
                        // The lease can expire between the failed acquisition and hint read; retry that gap immediately.
                        delay = TimeSpan.FromTicks(Math.Clamp(remaining.Ticks,
                            TimeSpan.FromMilliseconds(1).Ticks, AcquisitionPeriod.Ticks));
                    }
                }
                catch (Exception exception) when (exception is NpgsqlException { IsTransient: true } or TimeoutException or IOException)
                {
                    Volatile.Write(ref state, new(null, 0, TimeSpan.Zero, ""));
                    logger.LogError(exception, "Lease authority is unavailable; engine writes are disabled until reacquisition.");
                }
                await Task.Delay(delay, time, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref stopping, 1);
        await base.StopAsync(cancellationToken);
        await ReleaseAsync(cancellationToken);
    }

    public override void Dispose()
    {
        base.Dispose();
        changes.Dispose();
    }

    private sealed record State(LeaseToken? Lease, long ObservedAt, TimeSpan Remaining, string LeaderHint);
}
