using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Wf.Contracts.Registry;
using Wf.ControlPlane.Auth;

namespace Wf.ControlPlane.Live;

[Authorize]
public sealed class LiveHub(IAuthorizationService authorization) : Hub
{
    public const string Path = "/hubs/live/v1";

    public async Task<ProblemDetails?> Subscribe(string scope, string id)
    {
        var resource = Resource(scope, id);
        if (resource is null)
            return new() { Status = 400, Title = "Invalid subscription", Detail = "A supported scope and resource ID are required." };
        var result = await authorization.AuthorizeAsync(Context.User!, resource with { CancellationToken = Context.ConnectionAborted },
            TeamAuthorizationExtensions.Policy(RegistryRole.Viewer));
        if (!result.Succeeded)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(scope, id), Context.ConnectionAborted);
            return TeamAuthorizationExtensions.Forbidden();
        }
        await Groups.AddToGroupAsync(Context.ConnectionId, Group(scope, id), Context.ConnectionAborted);
        return null;
    }

    public Task Unsubscribe(string scope, string id) => Resource(scope, id) is null
        ? throw new HubException("A supported scope and resource ID are required.")
        : Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(scope, id), Context.ConnectionAborted);

    public static string Group(string scope, string id) => scope + ":" +
        (scope is "run" or "series" && Guid.TryParseExact(id, "D", out var uuid) ? uuid.ToString("D") : id);

    private static TeamResource? Resource(string scope, string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 1024 || id.Any(char.IsControl)) return null;
        TeamResourceKind? kind = scope switch
        {
            "team" => TeamResourceKind.Team,
            "type" => TeamResourceKind.Type,
            "run" => TeamResourceKind.Run,
            "series" => TeamResourceKind.Series,
            "machines" => TeamResourceKind.Machines,
            _ => null
        };
        return kind is null ? null : new(kind.Value, id);
    }
}
