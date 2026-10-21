using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi;
using Npgsql;
using Wf.Contracts.Registry;
using Wf.Contracts.Reports;
using Wf.ControlPlane.Auth;

namespace Wf.ControlPlane.Api;

public static class ReportCompareEndpoints
{
    public static IEndpointRouteBuilder MapReportCompareEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/series/{id}/reports/compare", Read).RequireAuthorization()
            .WithMetadata(new ResponseCacheAttribute { NoStore = true, Location = ResponseCacheLocation.None })
            .Produces<ReportCompareResponse>().ProducesProblem(400).Produces(401).ProducesProblem(403)
            .ProducesProblem(404).ProducesProblem(409).ProducesProblem(503)
            .WithDescription("Compare metadata for two distinct actual runs in one stored series, with fresh Viewer " +
                "authorization on the series and both runs. Exact case-sensitive sourceKey and title; no normalization. " +
                "No payload, variables, hash, blob path or capabilities. Missing either match is 404 " +
                "(report_match_not_found); multiple matches in either run, including attempts, are 409 " +
                "(report_match_ambiguous). Missing takes precedence. Neither failure identifies a side or returns metadata.")
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                operation.Parameters ??= [];
                foreach (var name in new[] { "leftRunId", "rightRunId", "sourceKey", "title" })
                    operation.Parameters.Add(new OpenApiParameter
                    {
                        Name = name, In = ParameterLocation.Query, Required = true,
                        Description = name switch
                        {
                            "leftRunId" or "rightRunId" => "Canonical lowercase RFC UUIDv7 of a distinct actual run in this series.",
                            "sourceKey" => "Single exact historical source, including stages; nonblank, at most 1025 UTF-8 bytes.",
                            _ => "Single exact author title; nonblank, at most 512 UTF-8 bytes."
                        },
                        Schema = new OpenApiSchema
                        {
                            Type = JsonSchemaType.String, Format = name.EndsWith("RunId", StringComparison.Ordinal) ? "uuid" : null
                        }
                    });
                return Task.CompletedTask;
            });
        return endpoints;
    }

    public static (Guid SeriesId, Guid LeftRunId, Guid RightRunId, string SourceKey, string Title) Parse(
        string id, IQueryCollection query)
    {
        if (query.Keys.Any(key => key is not ("leftRunId" or "rightRunId" or "sourceKey" or "title")))
            throw new ArgumentException("Unsupported comparison query.");
        var series = Uuid(id, false);
        var left = Uuid(Value("leftRunId", 36), true);
        var right = Uuid(Value("rightRunId", 36), true);
        if (left == right) throw new ArgumentException("Choose two distinct runs.");
        return (series, left, right, Value("sourceKey", 1025), Value("title", 512));

        string Value(string name, int maximum)
        {
            if (!query.TryGetValue(name, out var values) || values.Count != 1 ||
                string.IsNullOrWhiteSpace(values[0]) || values[0]!.Length > maximum ||
                values[0]!.Contains('\0') || new UTF8Encoding(false, true).GetByteCount(values[0]!) > maximum)
                throw new ArgumentException("Invalid comparison query.");
            return values[0]!;
        }

        static Guid Uuid(string text, bool run)
        {
            if (!Guid.TryParseExact(text, "D", out var value) || value == Guid.Empty || text != value.ToString("D") ||
                run && (text[14] != '7' || text[19] is not ('8' or '9' or 'a' or 'b')))
                throw new ArgumentException("Invalid comparison identity.");
            return value;
        }
    }

    private static async Task<IResult> Read(HttpContext context, string id, NpgsqlDataSource source,
        IAuthorizationService authorization, ILoggerFactory logging)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        (Guid SeriesId, Guid LeftRunId, Guid RightRunId, string SourceKey, string Title) input;
        try
        {
            input = Parse(id, context.Request.Query);
            if (context.Features.Get<IHttpRequestFeature>()?.RawTarget?.Split('?')[0] != $"/api/v1/series/{id}/reports/compare")
                throw new ArgumentException();
        }
        catch (ArgumentException) { return Problem(400, "Invalid report comparison query", "invalid_report_compare_query"); }
        try
        {
            var token = context.RequestAborted;
            foreach (var resource in new[]
            {
                new TeamResource(TeamResourceKind.Series, id, token),
                new TeamResource(TeamResourceKind.Run, input.LeftRunId.ToString("D"), token),
                new TeamResource(TeamResourceKind.Run, input.RightRunId.ToString("D"), token)
            })
                if (!(await authorization.AuthorizeAsync(context.User, resource,
                    TeamAuthorizationExtensions.Policy(RegistryRole.Viewer))).Succeeded)
                    return Results.Problem(TeamAuthorizationExtensions.Forbidden());
            await using (var members = source.CreateCommand("""
                SELECT count(*) FROM runs WHERE series_id=$1
                  AND ((run_day=uuid_v7_run_day($2) AND id=$2) OR (run_day=uuid_v7_run_day($3) AND id=$3))
                """))
            {
                members.Parameters.AddWithValue(input.SeriesId);
                members.Parameters.AddWithValue(input.LeftRunId);
                members.Parameters.AddWithValue(input.RightRunId);
                if (await members.ExecuteScalarAsync(token) is not 2L)
                    return Results.Problem(TeamAuthorizationExtensions.Forbidden());
            }
            var left = new List<ReportIndexItem>(2);
            var right = new List<ReportIndexItem>(2);
            await using var query = source.CreateCommand("""
                (SELECT id,run_id,source_key,attempt,kind,title,tags,size_bytes,created_at FROM reports
                 WHERE run_day=uuid_v7_run_day($1) AND run_id=$1
                   AND source_key COLLATE "C"=$3 AND title COLLATE "C"=$4 LIMIT 2)
                UNION ALL
                (SELECT id,run_id,source_key,attempt,kind,title,tags,size_bytes,created_at FROM reports
                 WHERE run_day=uuid_v7_run_day($2) AND run_id=$2
                   AND source_key COLLATE "C"=$3 AND title COLLATE "C"=$4 LIMIT 2)
                """);
            query.Parameters.AddWithValue(input.LeftRunId);
            query.Parameters.AddWithValue(input.RightRunId);
            query.Parameters.AddWithValue(input.SourceKey);
            query.Parameters.AddWithValue(input.Title);
            await using var reader = await query.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var run = reader.GetGuid(1);
                (run == input.LeftRunId ? left : right).Add(new()
                {
                    Id = reader.GetGuid(0), RunId = run, SourceKey = reader.GetString(2), Attempt = reader.GetInt16(3),
                    Kind = reader.GetString(4), Title = reader.GetString(5), Tags = reader.GetFieldValue<string[]>(6),
                    SizeBytes = reader.GetInt64(7), CreatedAt = reader.GetFieldValue<DateTimeOffset>(8)
                });
            }
            if (left.Count == 0 || right.Count == 0)
                return Problem(404, "Report comparison match not found", "report_match_not_found");
            if (left.Count != 1 || right.Count != 1)
                return Problem(409, "Report comparison match is ambiguous", "report_match_ambiguous");
            await reader.DisposeAsync();
            var signoffs = await Task.WhenAll(
                SignOffRead.Reports(source, input.LeftRunId, [left[0].Id], token),
                SignOffRead.Reports(source, input.RightRunId, [right[0].Id], token));
            left[0] = left[0] with { SignOff = signoffs[0].GetValueOrDefault(left[0].Id, Wf.Contracts.Runs.SignOffState.Empty) };
            right[0] = right[0] with { SignOff = signoffs[1].GetValueOrDefault(right[0].Id, Wf.Contracts.Runs.SignOffState.Empty) };
            return Results.Json(new ReportCompareResponse { SeriesId = input.SeriesId, Left = left[0], Right = right[0] },
                ReportCompareJsonContext.Default.ReportCompareResponse);
        }
        catch (Exception error) when (error is NpgsqlException or InvalidDataException or InvalidOperationException)
        {
            logging.CreateLogger("Wf.ReportCompare").LogError(error, "Report comparison unavailable.");
            return Problem(503, "Report comparison unavailable", "report_compare_unavailable");
        }
    }

    private static IResult Problem(int status, string title, string reason) => Results.Problem(
        statusCode: status, title: title, extensions: new Dictionary<string, object?> { ["reason"] = reason });
}
