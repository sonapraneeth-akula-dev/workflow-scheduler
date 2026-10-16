using System.Collections.Immutable;
using Npgsql;
using Wf.Contracts.Manifest;
using Wf.Validation;

namespace Wf.ControlPlane.Engine.Agents;

public static class MachineSelectorInventory
{
    public static async Task<ImmutableArray<ValidationError>> ValidateAsync(NpgsqlConnection connection,
        NpgsqlTransaction? transaction, WorkflowManifest manifest, IReadOnlyDictionary<string, object>? variables,
        CancellationToken cancellationToken)
    {
        if (!SelectorValidator.RequiresInventory(manifest))
            return SelectorValidator.Validate(manifest, [], variables, cancellationToken);
        var machines = ImmutableArray.CreateBuilder<SelectorMachine>();
        await using var command = new NpgsqlCommand("SELECT id,name,os,pool,labels FROM agents", connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var labels = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
            foreach (var label in reader.GetFieldValue<string[]>(4))
            {
                var separator = label.IndexOf('=');
                if (separator < 1) throw new InvalidDataException("A registered machine label is invalid.");
                labels.Add(label[..separator], label[(separator + 1)..]);
            }
            var os = reader.GetInt16(2) switch
            {
                0 => "windows",
                1 => "linux",
                _ => throw new InvalidDataException("A registered machine OS is invalid.")
            };
            machines.Add(new(reader.GetGuid(0), reader.GetString(1), os, reader.GetString(3), labels.ToImmutable()));
        }
        return SelectorValidator.Validate(manifest, machines.ToImmutable(), variables, cancellationToken);
    }
}
