using System.Globalization;
using System.Text;
using System.Text.Json;
using Npgsql;
using Wf.ControlPlane.Auth;

namespace Wf.ControlPlane.Engine.Notifications;

public sealed class NotificationDispatcher(NpgsqlDataSource source, NotificationOptions options, TimeProvider time,
    ILogger<NotificationDispatcher> logger) : BackgroundService
{
    public const int MaximumAttempts = 8;

    public static TimeSpan RetryDelay(int attempts)
    {
        if (attempts is < 1 or > MaximumAttempts) throw new ArgumentOutOfRangeException(nameof(attempts));
        return TimeSpan.FromSeconds(Math.Min(1 << (attempts - 1), 60));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await DispatchOneAsync(stoppingToken))
                    await Task.Delay(TimeSpan.FromSeconds(1), time, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (NpgsqlException)
            {
                logger.LogError("Notification storage is unavailable; pending delivery remains durable.");
                await Task.Delay(TimeSpan.FromSeconds(1), time, stoppingToken);
            }
        }
    }

    public async Task<bool> DispatchOneAsync(CancellationToken token)
    {
        if (!options.Enabled) throw new InvalidOperationException("Notifications are disabled.");
        await using var connection = await source.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        Guid subscription;
        string type, target;
        string? secret;
        // Lock the subscription before its outbox row, matching unsubscribe's cascade order.
        await using (var select = new NpgsqlCommand("""
            SELECT s.id,s.type_scope,s.target,s.secret_ref FROM subscriptions s
            WHERE s.channel='Webhook' AND EXISTS (
                SELECT 1 FROM notification_outbox o WHERE o.subscription_id=s.id
                    AND o.delivered_at IS NULL AND o.failed_at IS NULL AND o.next_attempt_at<=$1)
            ORDER BY s.id FOR UPDATE OF s SKIP LOCKED LIMIT 1
            """, connection))
        {
            select.Parameters.AddWithValue(time.GetUtcNow());
            await using var reader = await select.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token)) return false;
            subscription = reader.GetGuid(0);
            type = reader.GetString(1);
            target = reader.GetString(2);
            secret = reader.IsDBNull(3) ? null : reader.GetString(3);
        }
        Guid id;
        Guid runId;
        byte[] body;
        int attempts;
        await using (var select = new NpgsqlCommand("""
            SELECT id,CASE WHEN octet_length(payload::text)<=16384 THEN payload::text END,attempts,run_id
            FROM notification_outbox
            WHERE subscription_id=$1 AND delivered_at IS NULL AND failed_at IS NULL AND next_attempt_at<=$2
            ORDER BY next_attempt_at,id FOR UPDATE SKIP LOCKED LIMIT 1
            """, connection))
        {
            select.Parameters.AddWithValue(subscription);
            select.Parameters.AddWithValue(time.GetUtcNow());
            await using var reader = await select.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token)) return false;
            id = reader.GetGuid(0);
            body = reader.IsDBNull(1) ? [] : Encoding.UTF8.GetBytes(reader.GetString(1));
            attempts = reader.GetInt32(2);
            runId = reader.GetGuid(3);
        }
        string? error = null;
        var delivered = false;
        var retry = false;
        if (attempts >= MaximumAttempts) error = "attempt_limit";
        else if (options.Find(type, target, secret) is not { } destination) error = "destination_not_approved";
        else if (!ValidPayload(body, runId, type)) error = "invalid_cloud_event";
        else
        {
            try
            {
                var status = await destination.SendAsync(body,
                    time.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), token);
                delivered = status is >= 200 and < 300;
                if (!delivered)
                {
                    error = "http_" + status.ToString(CultureInfo.InvariantCulture);
                    retry = status is >= 500 and <= 599 or 408 or 429;
                }
            }
            catch (HttpRequestException)
            {
                error = "transport_unavailable";
                retry = true;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                error = "delivery_timeout";
                retry = true;
            }
        }
        var count = attempts >= MaximumAttempts ? attempts : attempts + 1;
        var now = time.GetUtcNow();
        var failed = !delivered && (!retry || count >= MaximumAttempts);
        await using var update = new NpgsqlCommand("""
            UPDATE notification_outbox SET attempts=$2,delivered_at=$3,failed_at=$4,last_error=$5,next_attempt_at=$6
            WHERE id=$1
            """, connection);
        update.Parameters.AddWithValue(id);
        update.Parameters.AddWithValue(count);
        update.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.TimestampTz, delivered ? now : DBNull.Value);
        update.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.TimestampTz, failed ? now : DBNull.Value);
        update.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Text, (object?)error ?? DBNull.Value);
        update.Parameters.AddWithValue(!failed && !delivered ? now + RetryDelay(count) : now);
        await update.ExecuteNonQueryAsync(token);
        await transaction.CommitAsync(token);
        if (failed) logger.LogWarning("Notification {NotificationId} failed permanently: {Reason}.", id, error);
        else if (!delivered) logger.LogWarning("Notification {NotificationId} delivery will retry: {Reason}.", id, error);
        return true;
    }

    private static bool ValidPayload(byte[] body, Guid runId, string typeScope)
    {
        if (body.Length is 0 or > 16 * 1024) return false;
        using var json = JsonDocument.Parse(body);
        var value = json.RootElement;
        if (!Shape(value, ["specversion", "id", "source", "type", "subject", "time", "datacontenttype", "data"]) ||
            Text(value, "specversion") != "1.0" || Text(value, "id") != runId.ToString("D") ||
            Text(value, "source") != "urn:kardia:runs" || Text(value, "datacontenttype") != "application/json" ||
            !WorkflowIdentity.TryGetTypeScope(Text(value, "subject"), out var owner) || owner != typeScope)
            return false;
        var data = value.GetProperty("data");
        if (!Shape(data, ["runId", "workflowId", "versionId", "seriesId", "status", "endedAt"]) ||
            Text(data, "runId") != runId.ToString("D") || Text(data, "workflowId") != Text(value, "subject") ||
            !Guid.TryParseExact(Text(data, "versionId"), "D", out var versionId) || versionId == Guid.Empty ||
            data.GetProperty("seriesId").ValueKind != JsonValueKind.Null &&
                (!Guid.TryParseExact(Text(data, "seriesId"), "D", out var seriesId) || seriesId == Guid.Empty) ||
            Text(data, "endedAt") != Text(value, "time") ||
            !DateTimeOffset.TryParseExact(Text(value, "time"), "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _))
            return false;
        return Text(data, "status") is "Succeeded" or "Failed" or "Aborted" or "TimedOut" &&
            Text(value, "type") == (Text(data, "status") == "Succeeded" ? "io.kardia.run.completed" : "io.kardia.run.failed");
    }

    private static bool Shape(JsonElement value, string[] fields) => value.ValueKind == JsonValueKind.Object &&
        value.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)
            .SequenceEqual(fields.Order(StringComparer.Ordinal));

    private static string? Text(JsonElement value, string name) =>
        value.GetProperty(name).ValueKind == JsonValueKind.String ? value.GetProperty(name).GetString() : null;
}
