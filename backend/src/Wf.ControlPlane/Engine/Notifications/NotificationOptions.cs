using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Wf.ControlPlane.Api.Subscriptions;

namespace Wf.ControlPlane.Engine.Notifications;

public sealed class NotificationOptions : IDisposable
{
    public bool Enabled { get; }
    private readonly WebhookDestination[] destinations;

    private NotificationOptions(bool enabled, WebhookDestination[] destinations)
    {
        Enabled = enabled;
        this.destinations = destinations;
    }

    public static NotificationOptions Read(IConfiguration configuration)
    {
        var enabled = configuration.GetValue("Notifications:Enabled", false);
        var sections = configuration.GetSection("Notifications:Webhooks").GetChildren().ToArray();
        if (sections.Length > 64 || enabled && sections.Length == 0)
            throw new InvalidOperationException("Enabled notifications require 1-64 operator-approved webhook destinations.");
        var destinations = new List<WebhookDestination>();
        try
        {
            foreach (var section in sections)
                destinations.Add(new WebhookDestination(section));
            if (destinations.Select(destination => (destination.TypeScope, destination.Target, destination.SecretRef))
                .Distinct().Count() != destinations.Count)
                throw new InvalidOperationException("Notification webhook configurations must be distinct.");
            return new(enabled, destinations.ToArray());
        }
        catch
        {
            foreach (var destination in destinations) destination.Dispose();
            throw;
        }
    }

    public WebhookDestination? Find(string typeScope, string target, string? secretRef) =>
        destinations.SingleOrDefault(destination => destination.TypeScope == typeScope &&
            destination.Target == target && destination.SecretRef == secretRef);

    public void Dispose()
    {
        foreach (var destination in destinations) destination.Dispose();
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
            var addresses = section.GetSection("Addresses").GetChildren().Select(child => Address(child.Value)).ToArray();
            if (addresses.Length is < 1 or > 8 || addresses.Distinct().Count() != addresses.Length ||
                endpoint.IsLoopback && addresses.Any(address => !IPAddress.IsLoopback(address)))
                throw new ArgumentException("Use 1-8 distinct pinned IP addresses consistent with the endpoint.");
            client = new HttpClient(new SocketsHttpHandler
            {
                AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
                MaxResponseHeadersLength = 16, ConnectTimeout = TimeSpan.FromSeconds(5),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                ConnectCallback = async (context, token) =>
                {
                    if (!string.Equals(context.DnsEndPoint.Host.Trim('[', ']'), endpoint.IdnHost, StringComparison.OrdinalIgnoreCase) ||
                        context.DnsEndPoint.Port != endpoint.Port)
                        throw new HttpRequestException("Notification connection does not match the approved endpoint.");
                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        await socket.ConnectAsync(addresses, endpoint.Port, token);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                }
            }) { Timeout = TimeSpan.FromSeconds(10) };
        }
        catch (Exception error) when (error is ArgumentException or FormatException)
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
            throw new InvalidOperationException("Invalid notification webhook configuration; check scope, destination, secret reference, signing key and pinned addresses.");
        }
    }

    private static IPAddress Address(string? value)
    {
        if (!IPAddress.TryParse(value, out var address) || value != address.ToString() || address.IsIPv4MappedToIPv6 ||
            address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.IsIPv6LinkLocal ||
            address.IsIPv6Multicast || address.IsIPv6SiteLocal || address.Equals(IPAddress.Parse("168.63.129.16")) ||
            address.Equals(IPAddress.Parse("fd00:ec2::254")))
            throw new ArgumentException("Use a canonical unicast IP, never a metadata, link-local or wildcard address.");
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork &&
            (bytes[0] is 0 or >= 224 || bytes[0] == 169 && bytes[1] == 254))
            throw new ArgumentException("Use a canonical unicast IP, never a metadata, link-local or wildcard address.");
        return address;
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
