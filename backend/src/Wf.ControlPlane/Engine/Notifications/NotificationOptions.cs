using System.Net;
using System.Security.Cryptography;
using Wf.ControlPlane.Api.Subscriptions;

namespace Wf.ControlPlane.Engine.Notifications;

public sealed class NotificationOptions : IDisposable
{
    public bool Enabled { get; }
    private readonly WebhookDestination[] destinations;
    private readonly EmailChannel[] emails;
    private readonly TeamsChannel[] teams;

    private NotificationOptions(bool enabled, WebhookDestination[] destinations, EmailChannel[] emails, TeamsChannel[] teams)
    {
        Enabled = enabled;
        this.destinations = destinations;
        this.emails = emails;
        this.teams = teams;
    }

    public static NotificationOptions Read(IConfiguration configuration)
    {
        var enabled = configuration.GetValue("Notifications:Enabled", false);
        var sections = configuration.GetSection("Notifications:Webhooks").GetChildren().ToArray();
        var emailSections = configuration.GetSection("Notifications:Emails").GetChildren().ToArray();
        var teamsSections = configuration.GetSection("Notifications:Teams").GetChildren().ToArray();
        var count = sections.Length + emailSections.Length + teamsSections.Length;
        if (count > 64 || enabled && count == 0)
            throw new InvalidOperationException("Enabled notifications require 1-64 operator-approved destinations.");
        var destinations = new List<WebhookDestination>();
        var emails = new List<EmailChannel>();
        var teams = new List<TeamsChannel>();
        try
        {
            foreach (var section in sections)
                destinations.Add(new WebhookDestination(section));
            foreach (var section in emailSections)
                emails.Add(new EmailChannel(section));
            foreach (var section in teamsSections)
                teams.Add(new TeamsChannel(section));
            if (destinations.Select(destination => (destination.TypeScope, destination.Target, destination.SecretRef))
                .Distinct().Count() != destinations.Count ||
                emails.Select(destination => (destination.TypeScope, destination.Target)).Distinct().Count() != emails.Count ||
                teams.Select(destination => (destination.TypeScope, destination.Target)).Distinct().Count() != teams.Count)
                throw new InvalidOperationException("Notification configurations must be distinct.");
            return new(enabled, destinations.ToArray(), emails.ToArray(), teams.ToArray());
        }
        catch
        {
            foreach (var destination in destinations) destination.Dispose();
            foreach (var destination in teams) destination.Dispose();
            throw;
        }
    }

    public WebhookDestination? Find(string typeScope, string target, string? secretRef) =>
        destinations.SingleOrDefault(destination => destination.TypeScope == typeScope &&
            destination.Target == target && destination.SecretRef == secretRef);

    public EmailChannel? FindEmail(string typeScope, string target) =>
        emails.SingleOrDefault(destination => destination.TypeScope == typeScope && destination.Target == target);

    public TeamsChannel? FindTeams(string typeScope, string target) =>
        teams.SingleOrDefault(destination => destination.TypeScope == typeScope && destination.Target == target);

    public void Dispose()
    {
        foreach (var destination in destinations) destination.Dispose();
        foreach (var destination in teams) destination.Dispose();
    }
}

public sealed class WebhookDestination : IDisposable
{
    public string TypeScope { get; }
    public string Target => endpoint.AbsoluteUri;
    public string SecretRef { get; }
    private readonly Uri endpoint;
    private readonly byte[] key;
    private readonly HttpClient client;

    internal WebhookDestination(IConfiguration section)
    {
        try
        {
            TypeScope = SubscriptionScope.Parse("type", section["TypeScope"] ?? "").Id;
            var request = new SubscriptionRequest("type", TypeScope, ["Completed"], "Webhook",
                section["Target"]!, section["SecretRef"]).Validate();
            endpoint = new Uri(request.Target, UriKind.Absolute);
            SecretRef = request.SecretRef!;
            var encoded = section["SigningKeyBase64"];
            if (encoded is null || encoded.Length is < 44 or > 172)
                throw new ArgumentException("Invalid signing key.");
            key = Convert.FromBase64String(encoded);
            if (key.Length is < 32 or > 128)
                throw new ArgumentException("Invalid signing key.");
            client = NotificationHttpClient.Create(endpoint, section);
        }
        catch (Exception error) when (error is ArgumentException or FormatException)
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
            throw new InvalidOperationException("Invalid notification webhook configuration; check scope, destination, secret reference, signing key and pinned addresses.");
        }
    }

    public async Task<int> SendAsync(byte[] body, string timestamp, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new ByteArrayContent(body)
        };
        request.Content.Headers.ContentType = new("application/cloudevents+json");
        request.Headers.Add(CloudEventSigner.TimestampHeader, timestamp);
        request.Headers.Add(CloudEventSigner.SignatureHeader, CloudEventSigner.Sign(key, timestamp, body));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        return (int)response.StatusCode;
    }

    public void Dispose()
    {
        client.Dispose();
        CryptographicOperations.ZeroMemory(key);
    }
}
