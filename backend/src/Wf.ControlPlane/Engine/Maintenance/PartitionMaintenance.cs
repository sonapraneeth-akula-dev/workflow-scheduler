using System.Globalization;
using System.Text.RegularExpressions;
using Npgsql;
using Wf.ControlPlane.Data;
using Wf.ControlPlane.Engine.Leadership;

namespace Wf.ControlPlane.Engine.Maintenance;

public sealed partial class PartitionMaintenance(NpgsqlDataSource source, TimeProvider time, LeaseManager? leadership = null)
{
    private static readonly string[] RunParents = ["task_checkpoints", "artifacts", "reports", "task_attempts", "node_runs", "run_snapshots", "run_events", "runs"];
    private sealed record Partition(string Parent, string Name, DateOnly Month);

    public static bool IsExpiredMonth(DateOnly month, DateTimeOffset now, int days) =>
        days is >= 1 and <= 36500 && month.Day == 1 && month.Year < 9999 &&
        month.AddMonths(1) <= DateOnly.FromDateTime(now.UtcDateTime.AddDays(-days));

    public async Task EnsureAsync(CancellationToken token = default)
    {
        var lease = leadership?.RequireLease();
        var month = new DateOnly(time.GetUtcNow().UtcDateTime.Year, time.GetUtcNow().UtcDateTime.Month, 1);
        await using var connection = await source.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await RetentionPurger.Configure(connection, token);
        if (leadership is not null) await leadership.FenceAsync(connection, transaction, lease!, token);
        foreach (var day in new[] { month, month.AddMonths(1) })
        {
            var names = RunParents.Select(parent => parent + "_" + day.ToString("yyyyMM", CultureInfo.InvariantCulture)).ToArray();
            await using (var check = new NpgsqlCommand(
                "SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname=current_schema() AND c.relname=ANY($1)", connection))
            {
                check.Parameters.AddWithValue(names);
                if ((long)(await check.ExecuteScalarAsync(token))! != names.Length)
                    await RetentionPurger.Execute(connection, "SELECT ensure_run_partitions($1)", token, day);
            }
            await using var audit = new NpgsqlCommand(
                "SELECT to_regclass(format('%I.%I',current_schema(),$1)) IS NOT NULL", connection);
            audit.Parameters.AddWithValue("audit_log_" + day.ToString("yyyyMM", CultureInfo.InvariantCulture));
            if (await audit.ExecuteScalarAsync(token) is not true)
                await RetentionPurger.Execute(connection, "SELECT ensure_audit_partition($1)", token, day);
        }
        await transaction.CommitAsync(token);
    }

    public async Task<int> DropExpiredEmptyAsync(CancellationToken token = default)
    {
        var now = time.GetUtcNow();
        if (!RetentionPurger.IsOffPeak(now)) return 0;
        var lease = leadership?.RequireLease();
        await using var connection = await source.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await RetentionPurger.Configure(connection, token);
        await RetentionPurger.Execute(connection, "SET LOCAL TIME ZONE 'UTC'", token);
        if (leadership is not null) await leadership.FenceAsync(connection, transaction, lease!, token);
        await RetentionPurger.LockPolicy(connection, token);
        var policy = await RetentionPolicyStore.ReadAsync(connection, token);
        var maximum = policy.PerType.Values.Append(policy.DefaultDays).Max();
        string schema;
        await using (var command = new NpgsqlCommand("SELECT current_schema()", connection))
            schema = await command.ExecuteScalarAsync(token) as string ?? throw new InvalidDataException("Partition schema is missing.");
        var partitions = new List<Partition>();
        await using (var command = new NpgsqlCommand("""
            SELECT p.relname,c.relname,pg_get_expr(c.relpartbound,c.oid)
            FROM pg_inherits i JOIN pg_class p ON p.oid=i.inhparent JOIN pg_class c ON c.oid=i.inhrelid
            JOIN pg_namespace pn ON pn.oid=p.relnamespace JOIN pg_namespace cn ON cn.oid=c.relnamespace
            WHERE pn.nspname=current_schema() AND cn.nspname=pn.nspname
                AND p.relname IN('runs','run_events','run_snapshots','node_runs','task_attempts','reports','artifacts','task_checkpoints','audit_log')
            """, connection))
        {
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var parent = reader.GetString(0);
                var name = reader.GetString(1);
                var bounds = Bounds().Match(reader.GetString(2));
                if (!bounds.Success ||
                    !DateOnly.TryParseExact(bounds.Groups["from"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from) ||
                    !DateOnly.TryParseExact(bounds.Groups["to"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var to) ||
                    from.Day != 1 || from.Year == 9999 || to != from.AddMonths(1) ||
                    name != parent + "_" + from.ToString("yyyyMM", CultureInfo.InvariantCulture))
                    throw new InvalidDataException("Unexpected retention partition name or bounds.");
                if (IsExpiredMonth(from, now, parent == "audit_log" ? policy.AuditDays : maximum))
                    partitions.Add(new(parent, name, from));
            }
        }
        foreach (var group in partitions.GroupBy(p => (p.Month, Audit: p.Parent == "audit_log")).OrderBy(g => g.Key.Month))
        {
            var ordered = group.OrderBy(p => Array.IndexOf(RunParents, p.Parent)).ToArray();
            if (!group.Key.Audit && ordered.Length != RunParents.Length)
                throw new InvalidDataException("An expired run partition family is incomplete.");
            var names = ordered.Select(p => Quote(schema) + "." + Quote(p.Name)).ToArray();
            var hasRows = string.Join(" OR ", names.Select(name => $"EXISTS(SELECT 1 FROM {name} LIMIT 1)"));
            await using (var check = new NpgsqlCommand("SELECT " + hasRows, connection))
                if (await check.ExecuteScalarAsync(token) is true) continue;
            await RetentionPurger.Execute(connection, $"LOCK TABLE {string.Join(",", names)} IN ACCESS EXCLUSIVE MODE NOWAIT", token);
            await using (var check = new NpgsqlCommand("SELECT " + hasRows, connection))
                if (await check.ExecuteScalarAsync(token) is true) continue;
            foreach (var partition in ordered)
            {
                var name = Quote(schema) + "." + Quote(partition.Name);
                await RetentionPurger.Execute(connection,
                    $"ALTER TABLE {Quote(schema)}.{Quote(partition.Parent)} DETACH PARTITION {name}", token);
                await RetentionPurger.Execute(connection, $"DROP TABLE {name}", token);
            }
            await transaction.CommitAsync(token);
            return ordered.Length;
        }
        await transaction.CommitAsync(token);
        return 0;
    }

    private static string Quote(string value) => new NpgsqlCommandBuilder().QuoteIdentifier(value);

    [GeneratedRegex(@"^FOR VALUES FROM \('(?<from>\d{4}-\d{2}-\d{2})(?: 00:00:00\+00)?'\) TO \('(?<to>\d{4}-\d{2}-\d{2})(?: 00:00:00\+00)?'\)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex Bounds();
}
