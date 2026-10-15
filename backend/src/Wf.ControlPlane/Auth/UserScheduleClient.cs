using System.Security.Claims;

namespace Wf.ControlPlane.Auth;

public sealed record UserScheduleRoute;

public sealed class UserScheduleClient(Guid clientId)
{
    public const string DevelopmentClientId = "00000000-0000-0000-0000-000000000002";
    public Guid ClientId { get; } = clientId;

    public bool Allows(ClaimsPrincipal caller)
    {
        if (ClientId == Guid.Empty || caller.Identity?.IsAuthenticated != true) return false;
        var clients = caller.FindAll("azp").ToArray();
        var scopes = caller.FindAll("scp").ToArray();
        var identities = caller.FindAll("idtyp").ToArray();
        var actors = caller.FindAll("oid").ToArray();
        return clients.Length == 1 && Guid.TryParseExact(clients[0].Value, "D", out var client) && client == ClientId &&
            scopes.Length == 1 && !string.IsNullOrWhiteSpace(scopes[0].Value) &&
            (identities.Length == 0 || identities.Length == 1 && identities[0].Value == "user") &&
            actors.Length == 1 && Guid.TryParseExact(actors[0].Value, "D", out var actor) && actor != Guid.Empty;
    }

    public static UserScheduleClient FromConfiguration(IConfiguration configuration)
    {
        var value = configuration["AzureAd:SpaClientId"];
        if (value is null)
            return new(configuration["Authentication:Mode"] == "Development" ? Guid.Parse(DevelopmentClientId) : Guid.Empty);
        if (!Guid.TryParseExact(value, "D", out var client) || client == Guid.Empty)
            throw new InvalidOperationException("AzureAd:SpaClientId must be a nonempty SPA application UUID when configured.");
        return new(client);
    }
}
