using System.Security.Claims;
using System.Text.Json;
using MessagePack;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Contracts.Events;
using Wf.Contracts.Agent.V1;
using Wf.ControlPlane.Live;

namespace Wf.ControlPlane.Tests;

public sealed class LiveHubTests
{
    [Theory]
    [InlineData("", "id")]
    [InlineData("logs:attempt", "id")]
    [InlineData("team", "")]
    [InlineData("team", "bad\nname")]
    [InlineData("run", "not-a-uuid")]
    [InlineData("series", "00000000-0000-0000-0000-000000000000")]
    public async Task Invalid_subscriptions_return_structured_400_without_authorization(string scope, string id)
    {
        await using var fixture = new Fixture();
        Assert.Equal(400, (await fixture.Hub.Subscribe(scope, id))!.Status);
        Assert.Equal(0, fixture.Auth.Calls);
        Assert.False(fixture.Subscriptions.HasAnySubscribers);
    }

    [Fact]
    public async Task Subscriptions_are_idempotent_canonical_and_removed_on_unsubscribe_or_disconnect()
    {
        await using var fixture = new Fixture();
        var id = Guid.CreateVersion7().ToString("D");
        Assert.Null(await fixture.Hub.Subscribe("run", id.ToUpperInvariant()));
        Assert.Null(await fixture.Hub.Subscribe("run", id));
        Assert.True(fixture.Subscriptions.HasSubscribers("run", id));
        Assert.Equal(LiveHub.Group("run", id), LiveHub.Group("run", id.ToUpperInvariant()));
        await fixture.Hub.Unsubscribe("run", id);
        Assert.False(fixture.Subscriptions.HasAnySubscribers);
        Assert.Null(await fixture.Hub.Subscribe("team", "alpha"));
        await fixture.Hub.OnDisconnectedAsync(null);
        Assert.False(fixture.Subscriptions.HasAnySubscribers);
        await Assert.ThrowsAsync<HubException>(() => fixture.Hub.Subscribe("team", "alpha"));
        await Assert.ThrowsAsync<HubException>(() => fixture.Hub.Unsubscribe("run", "invalid"));
    }

    [Fact]
    public async Task Revocation_is_enforced_on_publish_without_a_second_Subscribe_or_registry_notification()
    {
        await using var fixture = new Fixture();
        Assert.Null(await fixture.Hub.Subscribe("team", "alpha"));
        fixture.Auth.Allowed = false;
        await fixture.Subscriptions.PublishAsync("team", "alpha", "ListDelta",
            new ListDelta("team", "alpha", []), TestContext.Current.CancellationToken);
        Assert.False(fixture.Subscriptions.HasSubscribers("team", "alpha"));
        Assert.Equal(2, fixture.Auth.Calls);
        Assert.Equal(403, (await fixture.Hub.Subscribe("team", "alpha"))!.Status);
    }

    [Fact]
    public async Task Registry_reauthorization_revokes_existing_memberships_and_connection_capacity_is_bounded()
    {
        await using var fixture = new Fixture();
        for (var index = 0; index < 256; index++)
            Assert.Null(await fixture.Hub.Subscribe("team", "team" + index));
        Assert.Equal(429, (await fixture.Hub.Subscribe("team", "overflow"))!.Status);
        Assert.Null(await fixture.Hub.Subscribe("team", "team0"));
        fixture.Auth.Allowed = false;
        await fixture.Subscriptions.ReauthorizeAsync(TestContext.Current.CancellationToken);
        Assert.False(fixture.Subscriptions.HasAnySubscribers);
    }

    [Fact]
    public void Drain_hint_MessagePack_contains_only_its_fixed_reason_without_resource_payloads()
    {
        var bytes = MessagePackSerializer.Serialize(new LiveReconnect("server_draining"),
            cancellationToken: TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(MessagePackSerializer.ConvertToJson(bytes,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("""{"reason":"server_draining"}""", json.RootElement.GetRawText());
    }

    [Fact]
    public void MessagePack_and_OpenAPI_JSON_use_identical_camel_case_maps_and_ISO_timestamps_without_secrets()
    {
        var run = Guid.CreateVersion7();
        var agent = Guid.CreateVersion7();
        var at = DateTimeOffset.UtcNow;
        var abort = new AbortRecord("oid", "operator", at, "stop", AbortSource.UI);
        var delta = new RunDelta(run.ToString("D"),
            [
                LiveEvent.From(new(run, 42, at, EventEntity.Run, null, (int)RunStatus.Aborted, abort)),
                LiveEvent.From(new(run, 43, at, EventEntity.Task, "work", (int)ExecutionStatus.Ready)
                    { WaitingReason = "WaitingForMachine" }),
                LiveEvent.From(new(run, 44, at, EventEntity.Task, "work", (int)ExecutionStatus.Dispatched)
                    { AgentId = agent }),
                LiveEvent.From(new(run, 45, at, EventEntity.Task, "work/task", (int)ExecutionStatus.Running)
                    { Attempt = 2, Progress = new(1, RemoteOperationPhase.Waiting, at) })
            ]);
        var bytes = MessagePackSerializer.Serialize(delta, cancellationToken: TestContext.Current.CancellationToken);
        using var packed = JsonDocument.Parse(MessagePackSerializer.ConvertToJson(bytes,
            cancellationToken: TestContext.Current.CancellationToken));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(delta, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.True(JsonElement.DeepEquals(json.RootElement, packed.RootElement));
        Assert.Equal(at.ToString("O"), packed.RootElement.GetProperty("events")[0].GetProperty("at").GetString());
        Assert.Equal(42, packed.RootElement.GetProperty("events")[0].GetProperty("seq").GetInt64());
        Assert.Equal("UI", packed.RootElement.GetProperty("events")[0].GetProperty("abort").GetProperty("source").GetString());
        Assert.Equal("WaitingForMachine", packed.RootElement.GetProperty("events")[1].GetProperty("waitingReason").GetString());
        Assert.Equal(agent.ToString("D"), packed.RootElement.GetProperty("events")[2].GetProperty("agentId").GetString());
        var progress = packed.RootElement.GetProperty("events")[3];
        Assert.Equal(2, progress.GetProperty("attempt").GetInt32());
        Assert.Equal(1, progress.GetProperty("progress").GetProperty("sequence").GetInt32());
        Assert.Equal((int)RemoteOperationPhase.Waiting, progress.GetProperty("progress").GetProperty("phase").GetInt32());
        Assert.Equal(at.ToString("O"), progress.GetProperty("progress").GetProperty("occurredAt").GetString());
        Assert.DoesNotContain("variables", packed.RootElement.GetRawText());
        var problem = new LiveSubscriptionProblem(403, "Forbidden", "denied", "insufficient_scope_role");
        var problemBytes = MessagePackSerializer.Serialize(problem, cancellationToken: TestContext.Current.CancellationToken);
        using var error = JsonDocument.Parse(MessagePackSerializer.ConvertToJson(problemBytes,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(403, error.RootElement.GetProperty("status").GetInt32());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider services;
        public ScriptedAuthorization Auth { get; } = new();
        public LiveSubscriptions Subscriptions { get; }
        public LiveHub Hub { get; }

        public Fixture()
        {
            var collection = new ServiceCollection();
            collection.AddLogging();
            collection.AddSignalR();
            collection.AddSingleton<IAuthorizationService>(Auth);
            collection.AddSingleton<LiveSubscriptions>();
            services = collection.BuildServiceProvider();
            Subscriptions = services.GetRequiredService<LiveSubscriptions>();
            Hub = new(Auth, Subscriptions) { Context = new Caller() };
            Hub.OnConnectedAsync().GetAwaiter().GetResult();
        }

        public ValueTask DisposeAsync() => services.DisposeAsync();
    }

    private sealed class ScriptedAuthorization : IAuthorizationService
    {
        public bool Allowed { get; set; } = true;
        public int Calls { get; private set; }
        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName)
        {
            Calls++;
            return Task.FromResult(Allowed ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        }
        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource,
            IEnumerable<IAuthorizationRequirement> requirements) => throw new NotSupportedException();
    }

    private sealed class Caller : HubCallerContext
    {
        public override string ConnectionId => "live-unit";
        public override string? UserIdentifier => "unit";
        public override ClaimsPrincipal User { get; } = new(new ClaimsIdentity([new Claim("oid", "unit")], "test"));
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() { }
    }
}
