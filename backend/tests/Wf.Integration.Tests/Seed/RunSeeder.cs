using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;
using Wf.Contracts;
using Wf.Contracts.Registry;
using Wf.ControlPlane.Registry;

namespace Wf.Testing.RunQueries;

public sealed record SeededQueryRun(Guid Id, string Team, string Type, string WorkflowId, Guid SeriesId,
    RunStatus Status, DateTimeOffset CreatedAt, string Environment, string Region);

public sealed record RunQuerySeed(Guid ViewerGroup, ImmutableArray<string> Teams, ImmutableArray<string> Types,
    ImmutableArray<SeededQueryRun> Rows);

public static class RunSeeder
{
    public const int RowCount = 100_000;
    private static readonly string[] Environments = ["prod", "dev", "ppe", "test"];
    private static readonly string[] Regions = ["west", "east", "north", "south", "central"];

    public static async Task<RunQuerySeed> SeedAsync(NpgsqlDataSource source, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var prefix = "query-bench-" + Guid.NewGuid().ToString("N");
        var teams = Enumerable.Range(0, 4).Select(index => prefix + "-" + index).ToImmutableArray();
        ImmutableArray<string> types = ["release", "perf", "maintenance", "audit", "testing"];
        var group = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var start = new DateTimeOffset(now.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero)
            .AddSeconds(-RowCount - 1);
        var workflows = teams.SelectMany(team => types.SelectMany(type =>
            Enumerable.Range(0, 10).Select(index => (Team: team, Type: type,
                Id: team + "/" + type + "/workflow-" + index.ToString("D2", System.Globalization.CultureInfo.InvariantCulture),
                Version: Guid.CreateVersion7(start), Series: Guid.CreateVersion7(start))))).ToArray();
        var metadata = new Dictionary<(string Type, string Environment, string Region), string>();
        foreach (var type in types)
        foreach (var environment in Environments)
        foreach (var region in Regions)
            metadata.Add((type, environment, region), new JsonObject
            {
                ["Type"] = type,
                ["Values"] = new JsonObject { ["environment"] = environment, ["region"] = region }
            }.ToJsonString());
        var rows = ImmutableArray.CreateBuilder<SeededQueryRun>(RowCount);
        var schema = new MetadataSchema(
            [new("environment", AllowedValues: [.. Environments]), new("region", AllowedValues: [.. Regions])]);
        await using var connection = await source.OpenConnectionAsync(token);
        await using (var transaction = await connection.BeginTransactionAsync(token))
        {
            foreach (var day in new[] { DateOnly.FromDateTime(start.UtcDateTime), DateOnly.FromDateTime(now.UtcDateTime) }.Distinct())
            {
                await using var partitions = new NpgsqlCommand("SELECT ensure_run_partitions($1)", connection, transaction);
                partitions.Parameters.AddWithValue(day);
                await partitions.ExecuteNonQueryAsync(token);
            }
            foreach (var team in teams)
                await new RegistryWriter().UpsertAsync(connection, transaction,
                    new(team, [.. types.Select(type => new WorkflowTypeDefinition(team, type, "Run query benchmark",
                        schema))],
                        RoleBindings: team == teams[0] ? [new(RegistryRole.Viewer, group)] : []), token);
            await using (var copy = await connection.BeginBinaryImportAsync("""
                COPY runs(run_day,id,workflow_id,version_id,series_id,trigger,triggered_by,variables,metadata,status,
                    created_at,started_at,ended_at) FROM STDIN (FORMAT BINARY)
                """, token))
            {
                for (var index = 0; index < RowCount; index++)
                {
                    token.ThrowIfCancellationRequested();
                    var workflow = workflows[index % workflows.Length];
                    var status = (RunStatus)(index / workflows.Length % 7);
                    var environment = Environments[index / (workflows.Length * 7) % Environments.Length];
                    var region = Regions[index / (workflows.Length * 7 * Environments.Length) % Regions.Length];
                    var at = start.AddSeconds(index);
                    var id = Guid.CreateVersion7(at);
                    rows.Add(new(id, workflow.Team, workflow.Type, workflow.Id, workflow.Series, status, at, environment, region));
                    await copy.StartRowAsync(token);
                    await copy.WriteAsync(DateOnly.FromDateTime(at.UtcDateTime), NpgsqlDbType.Date, token);
                    await copy.WriteAsync(id, NpgsqlDbType.Uuid, token);
                    await copy.WriteAsync(workflow.Id, NpgsqlDbType.Text, token);
                    await copy.WriteAsync(workflow.Version, NpgsqlDbType.Uuid, token);
                    await copy.WriteAsync(workflow.Series, NpgsqlDbType.Uuid, token);
                    await copy.WriteAsync((short)(index % 3), NpgsqlDbType.Smallint, token);
                    await copy.WriteAsync("run-query-benchmark", NpgsqlDbType.Text, token);
                    await copy.WriteAsync("{}", NpgsqlDbType.Jsonb, token);
                    await copy.WriteAsync(metadata[(workflow.Type, environment, region)], NpgsqlDbType.Jsonb, token);
                    await copy.WriteAsync((short)status, NpgsqlDbType.Smallint, token);
                    await copy.WriteAsync(at, NpgsqlDbType.TimestampTz, token);
                    if (status == RunStatus.Queued) await copy.WriteNullAsync(token);
                    else await copy.WriteAsync(at.AddSeconds(1), NpgsqlDbType.TimestampTz, token);
                    if ((short)status < 3) await copy.WriteNullAsync(token);
                    else await copy.WriteAsync(at.AddSeconds(2), NpgsqlDbType.TimestampTz, token);
                }
                var copied = await copy.CompleteAsync(token);
                if (copied != RowCount) throw new InvalidDataException("Run seeding did not copy exactly 100,000 rows.");
            }
            await transaction.CommitAsync(token);
        }
        await using var analyze = new NpgsqlCommand("ANALYZE runs; ANALYZE workflow_types; ANALYZE role_bindings", connection);
        await analyze.ExecuteNonQueryAsync(token);
        return new(group, teams, types, rows.MoveToImmutable());
    }
}
