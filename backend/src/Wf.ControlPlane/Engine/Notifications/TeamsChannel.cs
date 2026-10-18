using System.Net;
using System.Text.Json;
using Wf.ControlPlane.Api.Subscriptions;

namespace Wf.ControlPlane.Engine.Notifications;

public sealed class TeamsChannel : IDisposable
{
    public string TypeScope { get; }
    public string Target { get; }
    private readonly Uri endpoint;
    private readonly HttpClient client;

    internal TeamsChannel(IConfiguration section)
    {
        try
        {
            TypeScope = SubscriptionScope.Parse("type", section["TypeScope"] ?? "").Id;
            Target = new SubscriptionRequest("type", TypeScope, ["Completed"], "Teams", section["Target"]!).Validate().Target;
            var url = section["Url"];
            if (url is null || url.Length is < 1 or > 8192 || url != url.Trim() || url.Any(char.IsControl) ||
                !Uri.TryCreate(url, UriKind.Absolute, out var parsed) || !string.IsNullOrEmpty(parsed.UserInfo) ||
                !string.IsNullOrEmpty(parsed.Fragment) ||
                parsed.Scheme != Uri.UriSchemeHttps && !(parsed.Scheme == Uri.UriSchemeHttp && parsed.IsLoopback))
                throw new ArgumentException("Invalid Teams workflow URL.");
            endpoint = parsed;
            client = NotificationHttpClient.Create(endpoint, section);
        }
        catch (Exception error) when (error is ArgumentException or FormatException)
        {
            throw new InvalidOperationException("Invalid notification Teams configuration; check scope, destination reference, workflow URL and pinned addresses.");
        }
    }

    public async Task<int> SendAsync(byte[] body, CancellationToken token)
    {
        using var json = JsonDocument.Parse(body);
        using var content = new MemoryStream();
        using (var writer = new Utf8JsonWriter(content))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "message");
            writer.WriteStartArray("attachments");
            writer.WriteStartObject();
            writer.WriteString("contentType", "application/vnd.microsoft.card.adaptive");
            writer.WriteNull("contentUrl");
            writer.WriteStartObject("content");
            writer.WriteString("$schema", "http://adaptivecards.io/schemas/adaptive-card.json");
            writer.WriteString("type", "AdaptiveCard");
            writer.WriteString("version", "1.2");
            writer.WriteStartArray("body");
            writer.WriteStartObject();
            writer.WriteString("type", "TextBlock");
            writer.WriteString("text", "Kardia workflow run completed");
            writer.WriteBoolean("wrap", true);
            writer.WriteEndObject();
            writer.WriteStartObject();
            writer.WriteString("type", "FactSet");
            writer.WriteStartArray("facts");
            foreach (var property in json.RootElement.GetProperty("data").EnumerateObject())
            {
                writer.WriteStartObject();
                writer.WriteString("title", property.Name);
                writer.WriteString("value", property.Value.ValueKind == JsonValueKind.Null ? "-" : property.Value.GetString());
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new ByteArrayContent(content.ToArray())
        };
        request.Content.Headers.ContentType = new("application/json");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        return (int)response.StatusCode;
    }

    public void Dispose() => client.Dispose();
}
