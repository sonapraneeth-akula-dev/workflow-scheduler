using System.Net;
using System.Net.Sockets;

namespace Wf.ControlPlane.Engine.Notifications;

internal static class NotificationHttpClient
{
    public static HttpClient Create(Uri endpoint, IConfiguration section)
    {
        var addresses = section.GetSection("Addresses").GetChildren().Select(child => Address(child.Value)).ToArray();
        if (addresses.Length is < 1 or > 8 || addresses.Distinct().Count() != addresses.Length ||
            endpoint.IsLoopback && addresses.Any(address => !IPAddress.IsLoopback(address)))
            throw new ArgumentException("Use 1-8 distinct pinned IP addresses consistent with the endpoint.");
        return new HttpClient(new SocketsHttpHandler
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

    internal static IPAddress Address(string? value)
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
}
