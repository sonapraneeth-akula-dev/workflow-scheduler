using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Wf.Contracts.Registry;
using Wf.ControlPlane.Auth;

namespace Wf.ControlPlane.Live;

public sealed class LiveSubscriptions(IHubContext<LiveHub> hub, IServiceScopeFactory scopes)
{
    private readonly ConcurrentDictionary<string, Connection> connections = new(StringComparer.Ordinal);

    public bool HasAnySubscribers => !connections.IsEmpty &&
        connections.Values.Any(connection => !connection.Subscriptions.IsEmpty);

    public bool HasSubscribers(string scope, string id) =>
        connections.Values.Any(connection => connection.Subscriptions.ContainsKey(LiveHub.Group(scope, id)));

    public void Connect(HubCallerContext context) =>
        connections[context.ConnectionId] = new(context.User!, context.ConnectionAborted);

    public void Disconnect(string connectionId) => connections.TryRemove(connectionId, out _);

    public async Task<LiveSubscriptionProblem?> SubscribeAsync(HubCallerContext context, TeamResource resource,
        string scope, string id, IAuthorizationService authorization)
    {
        if (!connections.TryGetValue(context.ConnectionId, out var connection))
            throw new HubException("The live connection is no longer active.");
        var group = LiveHub.Group(scope, id);
        await connection.Gate.WaitAsync(context.ConnectionAborted);
        try
        {
            var permitted = await authorization.AuthorizeAsync(connection.User,
                resource with { CancellationToken = context.ConnectionAborted },
                TeamAuthorizationExtensions.Policy(RegistryRole.Viewer));
            if (!permitted.Succeeded)
            {
                connection.Subscriptions.TryRemove(group, out _);
                await hub.Groups.RemoveFromGroupAsync(context.ConnectionId, group, context.ConnectionAborted);
                return new(403, "Forbidden", TeamRoleHandler.DenialReason, "insufficient_scope_role");
            }
            if (connection.Subscriptions.Count >= 256 && !connection.Subscriptions.ContainsKey(group))
                return new(429, "Too many live subscriptions", "A connection supports at most 256 scopes.");
            await hub.Groups.AddToGroupAsync(context.ConnectionId, group, context.ConnectionAborted);
            connection.Subscriptions[group] = new(scope, CanonicalId(scope, id), resource);
            return null;
        }
        finally { connection.Gate.Release(); }
    }

    public async Task UnsubscribeAsync(string connectionId, string scope, string id, CancellationToken token)
    {
        if (!connections.TryGetValue(connectionId, out var connection)) return;
        await connection.Gate.WaitAsync(token);
        try
        {
            var group = LiveHub.Group(scope, id);
            connection.Subscriptions.TryRemove(group, out _);
            await hub.Groups.RemoveFromGroupAsync(connectionId, group, token);
        }
        finally { connection.Gate.Release(); }
    }

    public Task PublishAsync<T>(string scope, string id, string target, T payload, CancellationToken token) =>
        Task.WhenAll(connections.Select(pair => SendAsync(pair.Key, pair.Value, LiveHub.Group(scope, id), target, payload, token)));

    public async Task ResyncAsync(CancellationToken token)
    {
        foreach (var group in connections.Values.SelectMany(connection => connection.Subscriptions.Values)
            .DistinctBy(subscription => LiveHub.Group(subscription.Scope, subscription.Id)))
            await PublishAsync(group.Scope, group.Id, "Resync", new LiveResync(group.Scope, group.Id), token);
    }

    public async Task ReauthorizeAsync(CancellationToken token)
    {
        foreach (var pair in connections)
        {
            foreach (var group in pair.Value.Subscriptions.Keys)
                await SendAsync<object?>(pair.Key, pair.Value, group, null, null, token);
        }
    }

    private async Task SendAsync<T>(string id, Connection connection, string group, string? target, T payload,
        CancellationToken token)
    {
        if (!connection.Subscriptions.ContainsKey(group) || connection.Token.IsCancellationRequested) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, connection.Token);
        try
        {
            await connection.Gate.WaitAsync(linked.Token);
            try
            {
                if (!connection.Subscriptions.TryGetValue(group, out var subscription)) return;
                await using var services = scopes.CreateAsyncScope();
                var authorization = services.ServiceProvider.GetRequiredService<IAuthorizationService>();
                var permitted = await authorization.AuthorizeAsync(connection.User,
                    subscription.Resource with { CancellationToken = linked.Token },
                    TeamAuthorizationExtensions.Policy(RegistryRole.Viewer));
                if (!permitted.Succeeded)
                {
                    connection.Subscriptions.TryRemove(group, out _);
                    await hub.Groups.RemoveFromGroupAsync(id, group, linked.Token);
                    await hub.Clients.Client(id).SendAsync("Resync", new LiveResync(subscription.Scope, subscription.Id), linked.Token);
                    return;
                }
                if (target is not null) await hub.Clients.Client(id).SendAsync(target, payload, linked.Token);
            }
            finally { connection.Gate.Release(); }
        }
        catch (OperationCanceledException) when (connection.Token.IsCancellationRequested && !token.IsCancellationRequested)
        {
            connections.TryRemove(id, out _);
        }
    }

    private static string CanonicalId(string scope, string id) =>
        scope is "run" or "series" ? Guid.ParseExact(id, "D").ToString("D") : id;

    private sealed record Subscription(string Scope, string Id, TeamResource Resource);
    private sealed class Connection(ClaimsPrincipal user, CancellationToken token)
    {
        public ClaimsPrincipal User { get; } = user;
        public CancellationToken Token { get; } = token;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public ConcurrentDictionary<string, Subscription> Subscriptions { get; } = new(StringComparer.Ordinal);
    }
}
