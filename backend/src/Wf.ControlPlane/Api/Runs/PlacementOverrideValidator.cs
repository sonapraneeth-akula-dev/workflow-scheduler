using System.Collections.Immutable;
using Npgsql;
using Wf.Contracts.Definition;
using Wf.Contracts.Manifest;
using Wf.Contracts.Runs;
using Wf.ControlPlane.Engine.Agents;
using Wf.Engine.Core.Placement;
using Wf.Validation;

namespace Wf.ControlPlane.Api;

public static class PlacementOverrideValidator
{
    public static string? Invalid(RunPlacement? placement) =>
        placement?.Machine is { } machine && (!placement.SingleMachine || string.IsNullOrWhiteSpace(machine) ||
            machine.Length > 200 || machine.Any(char.IsControl))
            ? "A machine name requires singleMachine=true and 1-200 nonblank characters without control characters."
            : null;

    public static bool Matches(PlacementAgent agent, RunPlacement placement, ImmutableArray<NodeDefinition> nodes) =>
        (placement.Machine is null || agent.Name == placement.Machine) &&
        nodes.All(node => (node.Placement?.Selector?.Count ?? 1) <= 1 &&
            (node.Placement?.Selector is not { Count: not null } reservation || PlacementFilter.Matches(reservation, agent)) &&
            node.Tasks.All(task => (task.Placement?.Selector?.Count ?? 1) <= 1 &&
                PlacementFilter.Matches(task.Placement?.Selector, agent)));

    public static async Task<ImmutableArray<ValidationError>> ValidateAsync(NpgsqlConnection connection,
        NpgsqlTransaction? transaction, WorkflowManifest manifest, IReadOnlyDictionary<string, object> variables,
        RunPlacement? placement, DateTimeOffset now, CancellationToken token)
    {
        if (Invalid(placement) is { } invalid) return [Error(invalid)];
        if (placement?.SingleMachine != true) return [];
        var nodes = PlacementResolver.Resolve(SelectorValidator.EffectiveNodes(manifest), variables);
        var machines = await ReadMachinesAsync(connection, transaction, now, token);
        if (machines.Any(machine => Matches(machine, placement, nodes))) return [];
        var conflicts = nodes.Where(node => !machines.Any(machine =>
            Matches(machine, placement, [node]))).Select(node => node.Key).ToArray();
        if (conflicts.Length == 0) conflicts = nodes.Select(node => node.Key).ToArray();
        return [Error("No healthy, non-draining, unreserved single machine satisfies the combined selectors and " +
            "single-machine reservation counts. Conflicting nodes: " + string.Join(", ", conflicts) + ".")];
    }

    internal static async Task<IResult> OptionsAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction,
        WorkflowManifest manifest, IReadOnlyDictionary<string, object> variables, DateTimeOffset now, CancellationToken token)
    {
        var nodes = PlacementResolver.Resolve(SelectorValidator.EffectiveNodes(manifest), variables);
        var machines = await ReadMachinesAsync(connection, transaction, now, token);
        return Results.Json(new PlacementOptionsResponse(machines.Where(machine => Matches(machine, new(true), nodes))
            .OrderBy(machine => machine.Name, StringComparer.Ordinal).ThenBy(machine => machine.Id)
            .Select(machine => new PlacementMachine(machine.Id, machine.Name)).ToImmutableArray()),
            RunJsonContext.Default.PlacementOptionsResponse);
    }

    private static async Task<List<PlacementAgent>> ReadMachinesAsync(NpgsqlConnection connection,
        NpgsqlTransaction? transaction, DateTimeOffset now, CancellationToken token)
    {
        var machines = new List<PlacementAgent>();
        await using (var query = new NpgsqlCommand("""
            SELECT a.id,a.name,a.os,a.pool,a.labels FROM agents a
            WHERE a.status=0 AND a.last_heartbeat_at>$1
                AND NOT EXISTS(SELECT 1 FROM reservations r WHERE r.agent_id=a.id AND r.released_at IS NULL)
            """, connection, transaction))
        {
            query.Parameters.AddWithValue(now - AgentRegistry.HeartbeatTimeout);
            await using var rows = await query.ExecuteReaderAsync(token);
            while (await rows.ReadAsync(token))
                machines.Add(new(rows.GetGuid(0), rows.GetString(1), rows.GetInt16(2) switch
                {
                    0 => "windows", 1 => "linux", _ => throw new InvalidDataException("Invalid machine OS.")
                }, rows.GetString(3), 0, 1, 0, Labels: AgentRegistry.ParseLabels(rows.GetFieldValue<string[]>(4))));
        }
        return machines;
    }

    private static ValidationError Error(string hint) =>
        new("RUN002", "request", "$.placement", "The single-machine placement cannot be satisfied.", hint);
}
