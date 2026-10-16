using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace Wf.ControlPlane.Hosting;

public sealed class LanListener(IPAddress address, int port, X509Certificate2 certificate) : IDisposable
{
    public IPAddress Address { get; } = address;
    public int Port { get; } = port;
    public Uri Endpoint => new($"https://{Address}:{Port}");

    public static LanListener? Read(IConfiguration configuration, IHostEnvironment environment)
    {
        if (!configuration.GetValue("Lan:Enabled", false)) return null;
        if (!environment.IsDevelopment())
            throw new InvalidOperationException("--lan is a trusted local Development profile, not a production listener.");
        var text = configuration["Lan:Address"];
        if (!IPAddress.TryParse(text, out var address) || address.AddressFamily != AddressFamily.InterNetwork ||
            text != address.ToString() || IPAddress.IsLoopback(address) || address.GetAddressBytes()[0] is 0 or >= 224 ||
            address.Equals(IPAddress.Any))
            throw new InvalidOperationException("Lan:Address must be a canonical non-loopback unicast IPv4 address.");
        var port = configuration.GetValue("Lan:Port", 5001);
        if (port is < 1 or > 65535) throw new InvalidOperationException("Lan:Port must be between 1 and 65535.");
        if (string.IsNullOrWhiteSpace(configuration["Lan:Certificate"]) || string.IsNullOrWhiteSpace(configuration["Lan:Key"]))
            throw new InvalidOperationException("Lan:Certificate and Lan:Key must name the mkcert PEM certificate and private key.");
        var certificate = X509Certificate2.CreateFromPemFile(configuration["Lan:Certificate"]!, configuration["Lan:Key"]!);
        if (!certificate.HasPrivateKey || certificate.NotBefore.ToUniversalTime() > DateTime.UtcNow ||
            certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow ||
            !certificate.MatchesHostname(address.ToString(), allowWildcards: false, allowCommonName: false))
        {
            certificate.Dispose();
            throw new InvalidOperationException("The LAN certificate must be currently valid and contain Lan:Address as an IP SAN.");
        }
        if (OperatingSystem.IsWindows())
        {
            // Schannel needs an OS-backed key, matching Kestrel's PEM certificate loader.
            using var pem = certificate;
            var pfx = pem.Export(X509ContentType.Pkcs12, "");
            try { certificate = X509CertificateLoader.LoadPkcs12(pfx, "", X509KeyStorageFlags.DefaultKeySet); }
            finally { CryptographicOperations.ZeroMemory(pfx); }
        }
        return new(address, port, certificate);
    }

    public void Configure(KestrelServerOptions options) =>
        options.Listen(Address, Port, listen =>
        {
            listen.Protocols = HttpProtocols.Http1AndHttp2;
            listen.UseHttps(certificate);
        });

    public void Dispose() => certificate.Dispose();
}
