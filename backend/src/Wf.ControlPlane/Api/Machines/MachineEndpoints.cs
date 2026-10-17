using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using NpgsqlTypes;
using Microsoft.OpenApi;
using Wf.Contracts;
using Wf.ControlPlane.Auth;
using Wf.ControlPlane.Engine.Agents;
using Wf.ControlPlane.Engine.Reservations;

namespace Wf.ControlPlane.Api.Machines;

public sealed record MachineReservationResponse(Guid Id, Guid RunId, string NodeKey, long Epoch,
    DateTimeOffset ExpiresAt, bool ReleasePending);
public sealed record MachineCapacityResponse(CapacityProfile Profile, int CapacityRps, string Source, DateTimeOffset? CalibratedAt);
public sealed record ManualCapacityRequest(CapacityProfile Profile,
    [property: JsonNumberHandling(JsonNumberHandling.Strict)] int CapacityRps);
public sealed record MachineResponse(Guid Id, string Name, string Os, string Arch, int Cores, int RamMb, int MaxTasks,
    int SlotsUsed, string Pool, IReadOnlyDictionary<string, string> Labels, string Status, DateTimeOffset LastHeartbeatAt,
    string Version, double CpuPercent, long RamUsedMb, long DiskFreeMb, MachineReservationResponse? CurrentReservation,
    IReadOnlyList<MachineCapacityResponse> Capacities);
public sealed record MachineListResponse(IReadOnlyList<MachineResponse> Items);

public sealed record MachinePatchRequest(Dictionary<string, string>? Labels = null, string? Pool = null,
    ManualCapacityRequest? ManualCapacity = null)
{
    public static MachinePatchRequest Parse(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new ArgumentException("A metadata object is required.");
        Dictionary<string, string>? labels = null;
        string? pool = null;
        ManualCapacityRequest? capacity = null;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Add(property.Name)) throw new ArgumentException("Duplicate metadata field.");
            if (property.Name == "pool" && property.Value.ValueKind == JsonValueKind.String)
                pool = property.Value.GetString();
            else if (property.Name == "labels" && property.Value.ValueKind == JsonValueKind.Object)
            {
                labels = new(StringComparer.Ordinal);
                foreach (var label in property.Value.EnumerateObject())
                    if (label.Value.ValueKind != JsonValueKind.String || !labels.TryAdd(label.Name, label.Value.GetString()!))
                        throw new ArgumentException("Labels must be unique string values.");
            }
            else if (property.Name == "manualCapacity" && property.Value.ValueKind == JsonValueKind.Object)
            {
                capacity = JsonSerializer.Deserialize(property.Value, MachineJsonContext.Default.ManualCapacityRequest)
                    ?? throw new ArgumentException("A manual capacity is required.");
                if (capacity.CapacityRps <= 0) throw new ArgumentException("Manual capacity must be a positive Int32 req/s value.");
            }
            else throw new ArgumentException("Only nonnull labels, pool and manualCapacity fields are supported.");
        }
        if (names.Count == 0) throw new ArgumentException("At least one metadata field is required.");
        AgentRegistry.ValidateMetadata(labels ?? [], pool ?? "default");
        return new(labels, pool, capacity);
    }
}

public sealed record MachineQuery(string? Pool, short? Os, short? Status, string[] Labels)
{
    public static MachineQuery Parse(IQueryCollection query)
    {
        string? pool = null;
        short? os = null;
        short? status = null;
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query)
        {
            if (pair.Key == "label")
            {
                foreach (var text in pair.Value)
                {
                    var parts = text?.Split('=', 2);
                    if (parts is not { Length: 2 } || !labels.TryAdd(parts[0], parts[1]))
                        throw new ArgumentException("Each label filter requires a distinct key=value.");
                }
                continue;
            }
            if (pair.Value.Count != 1) throw new ArgumentException("Scalar machine filters cannot repeat.");
            var value = pair.Value[0];
            switch (pair.Key)
            {
                case "pool": pool = value; break;
                case "os":
                    os = value switch { "windows" => 0, "linux" => 1, _ => throw new ArgumentException("Invalid machine OS.") };
                    break;
                case "status":
                    status = value switch
                    {
                        "Healthy" => 0, "Unhealthy" => 1, "Draining" => 2,
                        _ => throw new ArgumentException("Invalid machine status.")
                    };
                    break;
                default: throw new ArgumentException("Unsupported machine filter.");
            }
        }
        AgentRegistry.ValidateMetadata(labels, pool ?? "default");
        return new(pool, os, status, labels.Select(pair => pair.Key + "=" + pair.Value).ToArray());
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true, AllowDuplicateProperties = false)]
[JsonSerializable(typeof(MachineListResponse))]
[JsonSerializable(typeof(MachinePatchRequest))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(ManualCapacityRequest))]
[JsonSerializable(typeof(MachineCapacityResponse))]
public partial class MachineJsonContext : JsonSerializerContext;

public static class MachineEndpoints
{
    public static IEndpointRouteBuilder MapMachineEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/machines").RequireAuthorization(AuthExtensions.Admin)
            .WithMetadata(new ResponseCacheAttribute { NoStore = true });
        group.MapGet("", Read).Produces<MachineListResponse>().ProducesProblem(400)
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(500).ProducesProblem(503)
            .WithDescription("Global Admin inventory, ordered by immutable machine ID. Optional pool, os=windows|linux, " +
                "status=Healthy|Unhealthy|Draining and repeated label=key=value filters combine with AND. " +
                "Stale healthy heartbeats are reported Unhealthy. Current reservation remains until physical fence settlement. " +
                "Capacities list effective req/s per canonical profile; an empty list means Unverified, not zero capacity.")
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                operation.Parameters ??= [];
                foreach (var name in new[] { "pool", "os", "status", "label" })
                    operation.Parameters.Add(new OpenApiParameter
                    {
                        Name = name, In = ParameterLocation.Query,
                        Schema = name == "label" ?
                            new OpenApiSchema { Type = JsonSchemaType.Array, Items = new OpenApiSchema { Type = JsonSchemaType.String } } :
                            new OpenApiSchema { Type = JsonSchemaType.String },
                        Description = name switch
                        {
                            "pool" => "Exact pool name.",
                            "os" => "windows or linux.",
                            "status" => "Healthy, Unhealthy or Draining; includes heartbeat freshness.",
                            _ => "Repeat label=key=value for AND matching, with distinct keys; at most 100."
                        }
                    });
                return Task.CompletedTask;
            });
        group.MapPatch("/{id}", Patch).Accepts<MachinePatchRequest>("application/json")
            .Produces(204).ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404)
            .ProducesProblem(413).ProducesProblem(415).ProducesProblem(500).ProducesProblem(503)
            .WithDescription("Global Admin only. Atomically updates supplied labels, pool and/or manualCapacity with a before/after audit. " +
                "manualCapacity is { profile: { transport, codec, payloadSizeClass, loadSettings }, capacityRps: positive Int32 }. " +
                "Manual replaces calibrated capacity only for that canonical profile and clears its calibration timestamp. " +
                "Omitted fields and other profiles stay unchanged; empty labels clears labels. Overrides survive agent reconnects. " +
                "Does not interrupt running attempts or release reservations.");
        return endpoints;
    }

    private static async Task<IResult> Read(HttpContext context, NpgsqlDataSource source, TimeProvider time)
    {
        Headers(context);
        MachineQuery filter;
        try { filter = MachineQuery.Parse(context.Request.Query); }
        catch (ArgumentException) { return Results.Problem(statusCode: 400, title: "Invalid machine filters"); }
        try
        {
            await using var command = source.CreateCommand("""
                SELECT a.id,a.name,a.os,a.arch,a.cores,a.ram_mb,a.max_tasks,a.slots_used,a.pool,a.labels,
                    CASE WHEN a.status=0 AND a.last_heartbeat_at<=$1 THEN 1 ELSE a.status END::smallint,
                    a.last_heartbeat_at,a.agent_version,a.cpu_percent,a.ram_used_mb,a.disk_free_mb,
                    r.id,r.run_id,r.node_key,r.epoch,r.expires_at,r.release_requested_at IS NOT NULL
                FROM agents a LEFT JOIN reservations r ON r.agent_id=a.id AND r.released_at IS NULL
                WHERE ($2::text IS NULL OR a.pool=$2) AND ($3::smallint IS NULL OR a.os=$3)
                  AND ($4::smallint IS NULL OR
                    (CASE WHEN a.status=0 AND a.last_heartbeat_at<=$1 THEN 1 ELSE a.status END)=$4)
                  AND a.labels @> $5
                ORDER BY a.id
                """);
            command.Parameters.AddWithValue(time.GetUtcNow() - AgentRegistry.HeartbeatTimeout);
            command.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)filter.Pool ?? DBNull.Value);
            command.Parameters.AddWithValue(NpgsqlDbType.Smallint, (object?)filter.Os ?? DBNull.Value);
            command.Parameters.AddWithValue(NpgsqlDbType.Smallint, (object?)filter.Status ?? DBNull.Value);
            command.Parameters.AddWithValue(filter.Labels);
            var items = new List<MachineResponse>();
            await using (var reader = await command.ExecuteReaderAsync(context.RequestAborted))
            {
                while (await reader.ReadAsync(context.RequestAborted))
                    items.Add(new(reader.GetGuid(0), reader.GetString(1),
                        reader.GetInt16(2) switch { 0 => "windows", 1 => "linux", _ => throw new InvalidDataException() },
                        reader.GetInt16(3) switch { 0 => "x64", 1 => "arm64", _ => throw new InvalidDataException() },
                        reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7),
                        reader.GetString(8), AgentRegistry.ParseLabels(reader.GetFieldValue<string[]>(9)),
                        reader.GetInt16(10) switch { 0 => "Healthy", 1 => "Unhealthy", 2 => "Draining", _ => throw new InvalidDataException() },
                        reader.GetFieldValue<DateTimeOffset>(11), reader.GetString(12), reader.GetDouble(13),
                        reader.GetInt64(14), reader.GetInt64(15), reader.IsDBNull(16) ? null :
                            new(reader.GetGuid(16), reader.GetGuid(17), reader.GetString(18), reader.GetInt64(19),
                                reader.GetFieldValue<DateTimeOffset>(20), reader.GetBoolean(21)), []));
            }
            var capacities = items.ToDictionary(item => item.Id, _ => new List<MachineCapacityResponse>());
            if (items.Count != 0)
            {
                await using var capacityQuery = source.CreateCommand("""
                    SELECT agent_id,profile_key,capacity_rps,source,calibrated_at FROM agent_capacities
                    WHERE agent_id=ANY($1) ORDER BY agent_id,profile_key COLLATE "C"
                    """);
                capacityQuery.Parameters.AddWithValue(items.Select(item => item.Id).ToArray());
                await using var capacityReader = await capacityQuery.ExecuteReaderAsync(context.RequestAborted);
                while (await capacityReader.ReadAsync(context.RequestAborted))
                {
                    var agent = capacityReader.GetGuid(0);
                    capacities[agent].Add(ReadCapacity(capacityReader, agent, 1));
                }
            }
            return Results.Json(new MachineListResponse(items.Select(item =>
                item with { Capacities = capacities[item.Id] }).ToArray()), MachineJsonContext.Default.MachineListResponse);
        }
        catch (NpgsqlException) { return Results.Problem(statusCode: 503, title: "Machine inventory unavailable"); }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or IndexOutOfRangeException or JsonException)
        { return Results.Problem(statusCode: 500, title: "Committed machine metadata is inconsistent"); }
    }

    private static async Task<IResult> Patch(HttpContext context, string id, NpgsqlDataSource source)
    {
        Headers(context);
        if (!Guid.TryParseExact(id, "D", out var machine) || machine == Guid.Empty || id != machine.ToString("D") ||
            context.Request.Query.Count != 0)
            return Results.Problem(statusCode: 400, title: "Invalid machine identity or query");
        if (!Guid.TryParse(context.User.FindFirstValue("oid"), out var actor) || actor == Guid.Empty)
            return Results.Problem(statusCode: 403, title: "A valid audit principal is required");
        if (!context.Request.HasJsonContentType()) return Results.Problem(statusCode: 415, title: "JSON content is required");
        const int maximumBytes = 64 * 1024;
        if (context.Request.ContentLength > maximumBytes) return Results.Problem(statusCode: 413, title: "Machine metadata is too large");
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = maximumBytes;
        MachinePatchRequest request;
        try
        {
            using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            request = MachinePatchRequest.Parse(document.RootElement);
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        { return Results.Problem(statusCode: 400, title: "Invalid machine metadata"); }
        catch (BadHttpRequestException error) when (error.StatusCode == 413)
        { return Results.Problem(statusCode: 413, title: "Machine metadata is too large"); }
        try
        {
            var token = context.RequestAborted;
            await using var connection = await source.OpenConnectionAsync(token);
            await using var transaction = await connection.BeginTransactionAsync(token);
            await using (var allocation = new NpgsqlCommand("SELECT pg_advisory_xact_lock($1)", connection, transaction))
            {
                allocation.Parameters.AddWithValue(ReservationManager.AllocationLock);
                await allocation.ExecuteNonQueryAsync(token);
            }
            Dictionary<string, string> beforeLabels;
            string beforePool;
            await using (var before = new NpgsqlCommand("SELECT labels,pool FROM agents WHERE id=$1 FOR UPDATE", connection, transaction))
            {
                before.Parameters.AddWithValue(machine);
                await using var reader = await before.ExecuteReaderAsync(token);
                if (!await reader.ReadAsync(token)) return Results.Problem(statusCode: 404, title: "Machine not found");
                beforeLabels = AgentRegistry.ParseLabels(reader.GetFieldValue<string[]>(0)).ToDictionary();
                beforePool = reader.GetString(1);
            }
            MachineCapacityResponse? beforeCapacity = null;
            MachineCapacityResponse? afterCapacity = null;
            if (request.ManualCapacity is { } manual)
            {
                await using (var before = new NpgsqlCommand("""
                    SELECT profile_key,capacity_rps,source,calibrated_at FROM agent_capacities
                    WHERE agent_id=$1 AND profile_key=$2 FOR UPDATE
                    """, connection, transaction))
                {
                    before.Parameters.AddWithValue(machine);
                    before.Parameters.AddWithValue(manual.Profile.ProfileKey);
                    await using var reader = await before.ExecuteReaderAsync(token);
                    if (await reader.ReadAsync(token)) beforeCapacity = ReadCapacity(reader, machine, 0);
                }
                await using var upsert = new NpgsqlCommand("""
                    INSERT INTO agent_capacities(agent_id,profile_key,capacity_rps,source,calibrated_at)
                    VALUES($1,$2,$3,0,NULL)
                    ON CONFLICT(agent_id,profile_key) DO UPDATE
                        SET capacity_rps=EXCLUDED.capacity_rps,source=0,calibrated_at=NULL
                    """, connection, transaction);
                upsert.Parameters.AddWithValue(machine);
                upsert.Parameters.AddWithValue(manual.Profile.ProfileKey);
                upsert.Parameters.AddWithValue(manual.CapacityRps);
                if (await upsert.ExecuteNonQueryAsync(token) != 1)
                    throw new InvalidDataException("Manual capacity did not update exactly one profile.");
                afterCapacity = new(manual.Profile, manual.CapacityRps, "Manual", null);
            }
            await using var command = new NpgsqlCommand("""
                WITH updated AS (
                    UPDATE agents SET labels=COALESCE($2,labels),pool=COALESCE($3,pool),
                        labels_override=COALESCE($2,labels_override),pool_override=COALESCE($3,pool_override)
                    WHERE id=$1 RETURNING id
                )
                INSERT INTO audit_log(actor,actor_kind,action,target_kind,target_id,summary)
                SELECT $4,$5,'machine.update','Machine',id::text,
                    jsonb_build_object(
                        'before',jsonb_build_object('labels',$6::jsonb,'pool',$7::text) ||
                            CASE WHEN $10::boolean THEN jsonb_build_object('manualCapacity',$11::jsonb) ELSE '{}'::jsonb END,
                        'after',jsonb_build_object('labels',$8::jsonb,'pool',$9::text) ||
                            CASE WHEN $10::boolean THEN jsonb_build_object('manualCapacity',$12::jsonb) ELSE '{}'::jsonb END)
                FROM updated
                """, connection, transaction);
            command.Parameters.AddWithValue(machine);
            command.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Text,
                request.Labels is null ? DBNull.Value : request.Labels.Select(pair => pair.Key + "=" + pair.Value).ToArray());
            command.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)request.Pool ?? DBNull.Value);
            command.Parameters.AddWithValue(actor.ToString("D"));
            command.Parameters.AddWithValue(context.User.HasClaim("idtyp", "app") ? "App" : "User");
            command.Parameters.AddWithValue(JsonSerializer.Serialize(beforeLabels, MachineJsonContext.Default.DictionaryStringString));
            command.Parameters.AddWithValue(beforePool);
            command.Parameters.AddWithValue(JsonSerializer.Serialize(request.Labels ?? beforeLabels, MachineJsonContext.Default.DictionaryStringString));
            command.Parameters.AddWithValue(request.Pool ?? beforePool);
            command.Parameters.AddWithValue(request.ManualCapacity is not null);
            command.Parameters.AddWithValue(beforeCapacity is null ? "null" :
                JsonSerializer.Serialize(beforeCapacity, MachineJsonContext.Default.MachineCapacityResponse));
            command.Parameters.AddWithValue(afterCapacity is null ? "null" :
                JsonSerializer.Serialize(afterCapacity, MachineJsonContext.Default.MachineCapacityResponse));
            if (await command.ExecuteNonQueryAsync(token) != 1) throw new InvalidDataException("Machine edit did not create one audit row.");
            await transaction.CommitAsync(token);
            return Results.NoContent();
        }
        catch (NpgsqlException) { return Results.Problem(statusCode: 503, title: "Machine edit unavailable"); }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or IndexOutOfRangeException or JsonException)
        { return Results.Problem(statusCode: 500, title: "Committed machine metadata is inconsistent"); }
    }

    private static MachineCapacityResponse ReadCapacity(NpgsqlDataReader reader, Guid agent, int offset)
    {
        var key = reader.GetString(offset);
        var profile = JsonSerializer.Deserialize(key, CapacityProfileJsonContext.Default.CapacityProfile)
            ?? throw new InvalidDataException("Committed capacity profile is missing.");
        if (profile.ProfileKey != key) throw new InvalidDataException("Committed capacity profile is not canonical.");
        var capacity = new AgentCapacity(agent, profile, reader.GetInt32(offset + 1),
            (CapacitySource)reader.GetInt16(offset + 2),
            reader.IsDBNull(offset + 3) ? null : reader.GetFieldValue<DateTimeOffset>(offset + 3));
        return new(profile, capacity.CapacityRps, capacity.Source == CapacitySource.Manual ? "Manual" : "Calibrated", capacity.CalibratedAt);
    }

    private static void Headers(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
    }
}
