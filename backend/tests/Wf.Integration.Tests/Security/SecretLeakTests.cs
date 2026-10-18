using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Npgsql;
using Wf.Integration.Tests.Outputs;
using Wf.Integration.Tests.Secrets;

namespace Wf.Integration.Tests.Security;

public sealed class SecretLeakTests(TaskOutputsDatabase database, ITestOutputHelper output)
    : IClassFixture<TaskOutputsDatabase>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public Task Actual_success_failure_and_missing_secret_leave_zero_hits_across_database_events_and_uploaded_log_blobs() =>
        new SecretStoreIntegrationTests(database, output).RunScenarioAsync(uploadLogs: true, audit: async (host, value) =>
        {
            await using var connection = await database.DataSource.OpenConnectionAsync(Token);
            var baseline = await ScanDatabase(connection, value);
            Assert.Empty(baseline.Hits);
            foreach (var table in new[] { "runs", "run_events", "run_snapshots", "task_attempts", "node_runs", "agents" })
                Assert.True(baseline.Rows[table] > 0, $"No actual execution rows scanned in {table}.");
            Assert.True(baseline.Rows.ContainsKey("dispatch_outbox"));
            Assert.True(baseline.BinaryCells > 0, "No event/snapshot bytea payloads decoded.");

            var blobs = Path.Combine(host.Root, "blobs");
            var indices = Directory.GetFiles(blobs, "index.json", SearchOption.AllDirectories);
            Assert.Equal(3, indices.Length);
            foreach (var path in indices)
            {
                using var index = JsonDocument.Parse(await File.ReadAllTextAsync(path, Token));
                Assert.True(index.RootElement.GetProperty("complete").GetBoolean());
            }
            var files = new List<string>();
            foreach (var directory in new[] { blobs, Path.Combine(host.Root, "logs"), Path.Combine(host.Root, "journal") })
            {
                Assert.True(Directory.Exists(directory));
                files.AddRange(Directory.GetFiles(directory, "*", SearchOption.AllDirectories));
            }
            Assert.True(files.Count(path => path.EndsWith(".log.gz", StringComparison.Ordinal)) >= 2);
            var fileHits = await ScanFiles(files, value);
            Assert.Empty(fileHits);

            // Roll back deliberate corruption: prove the same scanner can find each durable representation.
            await using (var transaction = await connection.BeginTransactionAsync(Token))
            {
                await using var command = new NpgsqlCommand("""
                    WITH probe_runs AS (UPDATE runs SET metadata=jsonb_build_object('probe',$1::text)),
                    probe_attempts AS (UPDATE task_attempts SET error=$2),
                    probe_nodes AS (UPDATE node_runs SET outputs=json_build_object('probe',$3::text)),
                    probe_events AS (UPDATE run_events SET payload=$4)
                    UPDATE run_snapshots SET payload=$5
                    """, connection, transaction);
                command.Parameters.AddWithValue(value);
                command.Parameters.AddWithValue(Convert.ToBase64String(Encoding.UTF8.GetBytes(value)));
                command.Parameters.AddWithValue(JsonEncodedText.Encode(value).ToString());
                command.Parameters.AddWithValue(Encoding.UTF8.GetBytes(value));
                command.Parameters.AddWithValue(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { probe = value })));
                await command.ExecuteNonQueryAsync(Token);
                var probes = await ScanDatabase(connection, value, transaction);
                foreach (var table in new[] { "runs", "task_attempts", "node_runs", "run_events", "run_snapshots" })
                    Assert.Contains(probes.Hits, hit => hit.StartsWith(table + "/", StringComparison.Ordinal));
                Assert.Contains(probes.Hits, hit => hit.StartsWith("run_events/payload/", StringComparison.Ordinal));
                Assert.Contains(probes.Hits, hit => hit.StartsWith("run_snapshots/payload/", StringComparison.Ordinal));
                await transaction.RollbackAsync(Token);
            }
            Assert.Empty((await ScanDatabase(connection, value)).Hits);

            var probePath = Path.Combine(Path.GetDirectoryName(indices[0])!, "scanner-probe.log.gz");
            try
            {
                await using (var file = File.Create(probePath))
                await using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
                    await gzip.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { text = value })), Token);
                Assert.Single(await ScanFiles([probePath], value));
            }
            finally { File.Delete(probePath); }
            Assert.Empty(await ScanFiles(files, value));
            output.WriteLine($"Secret audit: {baseline.Rows.Count} tables, {baseline.Rows.Values.Sum()} rows, " +
                $"{baseline.BinaryCells} decoded bytea cells, {files.Count} spool/blob/journal files; zero hits. " +
                "Actual signed uploads and positive DB/event/snapshot/gzip leak controls verified; probes removed.");
        });

    [Theory]
    [InlineData("raw")]
    [InlineData("base64")]
    [InlineData("json")]
    [InlineData("nested-json")]
    public void Scanner_finds_raw_encoded_and_nested_JSON_values_without_echoing_them(string encoding)
    {
        const string value = "scanner-probe-cl\u00e9-\ud83d\udd11";
        var text = encoding switch
        {
            "raw" => value,
            "base64" => Convert.ToBase64String(Encoding.UTF8.GetBytes(value)),
            "json" => JsonEncodedText.Encode(value).ToString(),
            _ => JsonSerializer.Serialize(new { probe = JsonEncodedText.Encode(value).ToString() })
        };
        Assert.True(ContainsSecret(text, value));
        Assert.False(ContainsSecret("masked ***; secret://local/credential", value));
    }

    private static async Task<DatabaseScan> ScanDatabase(NpgsqlConnection connection, string value,
        NpgsqlTransaction? transaction = null)
    {
        var tables = new List<(string Name, string[] BinaryColumns)>();
        await using (var schema = new NpgsqlCommand("""
            SELECT c.relname, ARRAY(SELECT a.attname::text FROM pg_attribute a
              WHERE a.attrelid=c.oid AND a.atttypid='bytea'::regtype AND a.attnum>0 AND NOT a.attisdropped
              ORDER BY a.attnum)
            FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
            WHERE n.nspname=current_schema() AND c.relkind IN ('r','p') AND NOT c.relispartition
            ORDER BY c.relname
            """, connection, transaction))
        await using (var reader = await schema.ExecuteReaderAsync(Token))
            while (await reader.ReadAsync(Token))
                tables.Add((reader.GetString(0), reader.GetFieldValue<string[]>(1)));
        Assert.NotEmpty(tables);
        var result = new DatabaseScan([], [], 0);
        using var identifiers = new NpgsqlCommandBuilder();
        foreach (var (name, binaryColumns) in tables)
        {
            var fields = string.Concat(binaryColumns.Select(column => ", t." + identifiers.QuoteIdentifier(column)));
            await using var command = new NpgsqlCommand(
                $"SELECT row_to_json(t)::text{fields} FROM {identifiers.QuoteIdentifier(name)} t",
                connection, transaction);
            await using var reader = await command.ExecuteReaderAsync(Token);
            var rows = 0;
            while (await reader.ReadAsync(Token))
            {
                rows++;
                if (ContainsSecret(reader.GetString(0), value)) result.Hits.Add(name + "/row-json");
                for (var index = 0; index < binaryColumns.Length; index++)
                {
                    if (reader.IsDBNull(index + 1)) continue;
                    result = result with { BinaryCells = result.BinaryCells + 1 };
                    if (ContainsSecret(Encoding.UTF8.GetString(reader.GetFieldValue<byte[]>(index + 1)), value))
                        result.Hits.Add(name + "/" + binaryColumns[index] + "/decoded-bytea");
                }
            }
            result.Rows.Add(name, rows);
        }
        return result;
    }

    private static async Task<List<string>> ScanFiles(IEnumerable<string> files, string value)
    {
        var hits = new List<string>();
        foreach (var path in files)
        {
            await using var file = File.OpenRead(path);
            string text;
            if (path.EndsWith(".gz", StringComparison.Ordinal))
            {
                await using var gzip = new GZipStream(file, CompressionMode.Decompress);
                using var reader = new StreamReader(gzip, Encoding.UTF8);
                text = await reader.ReadToEndAsync(Token);
            }
            else
            {
                using var reader = new StreamReader(file, Encoding.UTF8);
                text = await reader.ReadToEndAsync(Token);
            }
            if (ContainsSecret(text, value)) hits.Add(Path.GetFileName(path));
        }
        return hits;
    }

    private static bool ContainsSecret(string text, string value)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        var escaped = JsonEncodedText.Encode(value).ToString();
        while (true)
        {
            if (text.Contains(value, StringComparison.Ordinal) || text.Contains(encoded, StringComparison.Ordinal) ||
                text.Contains(escaped, StringComparison.OrdinalIgnoreCase))
                return true;
            var unescaped = text.Replace(@"\\", @"\", StringComparison.Ordinal);
            if (unescaped.Length == text.Length) return false;
            text = unescaped;
        }
    }

    private sealed record DatabaseScan(Dictionary<string, int> Rows, List<string> Hits, int BinaryCells);
}
