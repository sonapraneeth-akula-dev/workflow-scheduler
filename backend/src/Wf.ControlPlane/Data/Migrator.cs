using System.Text.RegularExpressions;
using DbUp;
using DbUp.Engine;
using Npgsql;

namespace Wf.ControlPlane.Data;

public static partial class Migrator
{
    public static IReadOnlyList<SqlScript> LoadScripts()
    {
        const string prefix = "Wf.Migrations.";
        var assembly = typeof(Migrator).Assembly;
        return assembly.GetManifestResourceNames().Where(name => name.StartsWith(prefix, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).Select(name =>
            {
                using var stream = assembly.GetManifestResourceStream(name)
                    ?? throw new InvalidDataException($"Missing embedded migration {name}.");
                using var reader = new StreamReader(stream);
                return new SqlScript(name[prefix.Length..], reader.ReadToEnd());
            }).ToArray();
    }

    public static IReadOnlyList<string> Upgrade(string connectionString, ILogger logger,
        IEnumerable<SqlScript>? scripts = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(logger);
        var ordered = (scripts ?? LoadScripts()).OrderBy(script => script.Name, StringComparer.Ordinal).ToArray();
        if (ordered.Length == 0 || ordered.Any(script => !ScriptName().IsMatch(script.Name)) ||
            ordered.Select(script => script.Name).Distinct(StringComparer.Ordinal).Count() != ordered.Length)
            throw new ArgumentException("Migrations require unique NNNN_lowercase_name.sql filenames.", nameof(scripts));
        var prepared = ordered.Select(script =>
        {
            var errors = MigrationLint.Validate(script.Contents);
            if (errors.Count > 0)
                throw new InvalidDataException(string.Join(Environment.NewLine,
                    errors.Select(error => $"{script.Name}:{error.Line}: {error.Message}")));
            return new SqlScript(script.Name, MigrationLint.Unwrap(script.Contents));
        }).ToArray();
        using var migrationLock = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString)
            { Pooling = false }.ConnectionString);
        migrationLock.Open();
        using var schemaQuery = new NpgsqlCommand("SELECT current_schema()", migrationLock);
        var schema = schemaQuery.ExecuteScalar() as string
            ?? throw new InvalidOperationException("The migration search path must resolve to an existing schema.");
        using var acquire = new NpgsqlCommand("""
            SELECT pg_advisory_lock(hashtextextended(current_database() || ':' || current_schema() || ':wf-migrations', 0))
            """, migrationLock) { CommandTimeout = 60 };
        acquire.ExecuteNonQuery();
        var upgrader = DeployChanges.To.PostgresqlDatabase(connectionString, schema)
            .WithScripts(prepared).WithVariablesDisabled().WithTransactionPerScript()
            .WithExecutionTimeout(TimeSpan.FromSeconds(60))
            .JournalToPostgresqlTable(schema, "schema_versions").LogTo(logger).Build();
        var result = upgrader.PerformUpgrade();
        if (!result.Successful)
            throw new InvalidOperationException($"Migration '{result.ErrorScript?.Name ?? "journal"}' failed.", result.Error);
        return result.Scripts.Select(script => script.Name).ToArray();
    }

    [GeneratedRegex(@"^[0-9]{4}_[a-z0-9_]+\.sql$", RegexOptions.CultureInvariant)]
    private static partial Regex ScriptName();
}
