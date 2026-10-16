using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Wf.ControlPlane.Auth;

namespace Wf.ControlPlane.Live;

[Authorize]
public sealed class LiveHub(IAuthorizationService authorization, LiveSubscriptions subscriptions) : Hub
{
    public const string Path = "/hubs/live/v1";

    public override async Task OnConnectedAsync()
    {
        await subscriptions.ConnectAsync(Context);
        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        subscriptions.Disconnect(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    public async Task<LiveSubscriptionProblem?> Subscribe(string scope, string id)
    {
        var resource = Resource(scope, id);
        if (resource is null)
            return new(400, "Invalid subscription", "A supported scope and resource ID are required.");
        return await subscriptions.SubscribeAsync(Context, resource, scope, id, authorization);
    }

    public Task Unsubscribe(string scope, string id) => Resource(scope, id) is null
        ? throw new HubException("A supported scope and resource ID are required.")
        : subscriptions.UnsubscribeAsync(Context.ConnectionId, scope, id, Context.ConnectionAborted);

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
        if (kind is TeamResourceKind.Run or TeamResourceKind.Series &&
            (!Guid.TryParseExact(id, "D", out var uuid) || uuid == Guid.Empty)) return null;
        return kind is null ? null : new(kind.Value, id);
    }
}
