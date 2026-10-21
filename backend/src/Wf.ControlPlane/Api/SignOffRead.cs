using System.Globalization;
using Npgsql;
using Wf.Contracts.Runs;

namespace Wf.ControlPlane.Api;

internal static class SignOffRead
{
    public static Task<Dictionary<Guid, SignOffState>> Runs(
        NpgsqlDataSource source, IEnumerable<Guid> runs, CancellationToken token) =>
        Read(source, runs.Distinct().ToArray(), null, token);

    public static Task<Dictionary<Guid, SignOffState>> Reports(
        NpgsqlDataSource source, Guid run, IEnumerable<Guid> reports, CancellationToken token) =>
        Read(source, reports.Distinct().ToArray(), run, token);

    private static async Task<Dictionary<Guid, SignOffState>> Read(
        NpgsqlDataSource source, Guid[] ids, Guid? run, CancellationToken token)
    {
        var result = new Dictionary<Guid, SignOffState>();
        if (ids.Length == 0) return result;
        await using var query = source.CreateCommand("""
            SELECT s.target_id,s.revision,e.action,e.reason,e.actor_id,e.actor_name,e.at
            FROM run_signoffs s LEFT JOIN run_signoff_events e
                ON (e.run_id,e.target_kind,e.target_id,e.revision)=(s.run_id,s.target_kind,s.target_id,s.revision)
            WHERE
            """ + (run is null ? " s.target_kind=0 AND s.run_id=ANY($1)" :
                " s.target_kind=1 AND s.run_id=$2 AND s.target_id=ANY($1)"));
        query.Parameters.AddWithValue(ids);
        if (run is { } id) query.Parameters.AddWithValue(id);
        await using var reader = await query.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var revision = reader.GetInt64(1);
            if (revision == 0 && reader.IsDBNull(2))
            {
                result.Add(reader.GetGuid(0), SignOffState.Empty);
                continue;
            }
            if (revision < 1 || reader.IsDBNull(2))
                throw new InvalidDataException("Sign-off revision has no matching event.");
            var action = (SignOffAction)reader.GetInt16(2);
            if (!Enum.IsDefined(action)) throw new InvalidDataException("Unknown sign-off action.");
            var latest = new SignOffEvent(revision.ToString(CultureInfo.InvariantCulture), action,
                reader.GetString(3), reader.GetGuid(4), reader.GetString(5), reader.GetFieldValue<DateTimeOffset>(6));
            result.Add(reader.GetGuid(0), new(latest.Revision,
                action == SignOffAction.SignOff ? SignOffStatus.SignedOff : SignOffStatus.Revoked, latest));
        }
        return result;
    }
}
