using System.Net;

namespace Wf.ControlPlane.Hosting;

public sealed record LocalComposeOptions(string Environment, IPNetwork Network)
{
    public static LocalComposeOptions? Read(IConfiguration configuration, IHostEnvironment environment)
    {
        if (!configuration.GetValue("LocalCompose:Enabled", false)) return null;
        if (!environment.IsDevelopment() || configuration["DOTNET_RUNNING_IN_CONTAINER"] != "true")
            throw new InvalidOperationException("The local Compose profile requires an explicit Development container.");
        if (configuration.GetValue("Lan:Enabled", false))
            throw new InvalidOperationException("Local Compose and the native trusted-LAN listener are separate profiles.");
        var name = configuration["LocalCompose:Environment"];
        return name switch
        {
            "dev" => new(name, IPNetwork.Parse("172.30.62.0/24")),
            "ppe" => new(name, IPNetwork.Parse("172.30.67.0/24")),
            _ => throw new InvalidOperationException("LocalCompose:Environment must be dev or ppe.")
        };
    }

    public bool Allows(IPAddress? local, IPAddress? remote) =>
        local is not null && remote is not null && Network.Contains(local) && Network.Contains(remote);

    public static bool IsPackageOrigin(Uri endpoint) => endpoint.Scheme == Uri.UriSchemeHttp &&
        endpoint.Host == "wf-api" && endpoint.Port == 8080 && endpoint.AbsolutePath == "/" &&
        endpoint.UserInfo.Length == 0 && endpoint.Query.Length == 0 && endpoint.Fragment.Length == 0;

    public static bool IsEngineOrigin(Uri endpoint) => endpoint.Scheme == Uri.UriSchemeHttp &&
        endpoint.Host is "wf-engine-a" or "wf-engine-b" && endpoint.Port == 8081 &&
        endpoint.AbsolutePath == "/" && endpoint.UserInfo.Length == 0 &&
        endpoint.Query.Length == 0 && endpoint.Fragment.Length == 0;
}
