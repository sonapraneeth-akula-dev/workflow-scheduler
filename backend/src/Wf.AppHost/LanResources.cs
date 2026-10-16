using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;

namespace Wf.AppHost;

internal sealed record LanResources(string Address, int Port, string Certificate, string Key)
{
    internal static LanResources? Read(IConfiguration configuration, string root)
    {
        if (!configuration.GetValue("Local:Lan", false)) return null;
        var text = configuration["Local:LanAddress"];
        if (text is null)
        {
            var addresses = NetworkInterface.GetAllNetworkInterfaces()
                .Where(network => network.OperationalStatus == OperationalStatus.Up &&
                    network.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                    network.GetIPProperties().GatewayAddresses.Any(gateway =>
                        gateway.Address.AddressFamily == AddressFamily.InterNetwork && !gateway.Address.Equals(IPAddress.Any)))
                .SelectMany(network => network.GetIPProperties().UnicastAddresses)
                .Where(unicast => unicast.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(unicast.Address) && unicast.Address.GetAddressBytes()[0] != 169)
                .Select(unicast => unicast.Address.ToString()).Distinct().ToArray();
            if (addresses.Length != 1)
                throw new InvalidOperationException("--lan needs one default-route IPv4 address; set Local:LanAddress explicitly.");
            text = addresses[0];
        }
        if (!IPAddress.TryParse(text, out var address) || address.AddressFamily != AddressFamily.InterNetwork ||
            text != address.ToString() || IPAddress.IsLoopback(address) || address.GetAddressBytes()[0] is 0 or >= 224 ||
            !NetworkInterface.GetAllNetworkInterfaces().SelectMany(network => network.GetIPProperties().UnicastAddresses)
                .Any(unicast => unicast.Address.Equals(address)))
            throw new InvalidOperationException("Local:LanAddress must be this machine's canonical non-loopback unicast IPv4 address.");
        var port = configuration.GetValue("Local:LanPort", 5001);
        if (port is < 1 or > 65535) throw new InvalidOperationException("Local:LanPort must be between 1 and 65535.");
        var certificate = configuration["Local:LanCertificate"];
        var key = configuration["Local:LanKey"];
        if (certificate is not null || key is not null)
        {
            if (string.IsNullOrWhiteSpace(certificate) || string.IsNullOrWhiteSpace(key) ||
                !File.Exists(certificate) || !File.Exists(key))
                throw new InvalidOperationException("Local:LanCertificate and Local:LanKey must both name existing PEM files.");
            return new(text, port, Path.GetFullPath(certificate), Path.GetFullPath(key));
        }
        var directory = Path.Combine(root, "tls");
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        certificate = Path.Combine(directory, text + ".pem");
        key = Path.Combine(directory, text + "-key.pem");
        var start = new ProcessStartInfo("mkcert") { UseShellExecute = false };
        if (configuration["Local:LanCaRoot"] is { } caRoot)
        {
            if (string.IsNullOrWhiteSpace(caRoot)) throw new InvalidOperationException("Local:LanCaRoot cannot be empty.");
            start.Environment["CAROOT"] = Path.GetFullPath(caRoot);
        }
        foreach (var argument in new[] { "-cert-file", certificate, "-key-file", key, text }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start mkcert; install it before using --lan.");
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("mkcert did not finish within 30 seconds.");
        }
        if (process.ExitCode != 0) throw new InvalidOperationException($"mkcert exited {process.ExitCode}; --lan was not started.");
        return new(text, port, certificate, key);
    }
}
