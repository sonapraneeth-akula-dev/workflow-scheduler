using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Microsoft.IdentityModel.Validators;
using Npgsql;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Contracts.Events;
using Wf.Contracts.Registry;
using Wf.ControlPlane.Api;
using Wf.ControlPlane.Auth;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Events;
using Wf.ControlPlane.Live;
using Wf.ControlPlane.Hosting;
using Microsoft.Extensions.Hosting;
using Wf.ControlPlane.Registry;
using Wf.Engine.Core;
using Wf.Integration.Tests.Auth;

namespace Wf.Integration.Tests;

public sealed class LiveResyncTests(AuthorizationDatabase database, ITestOutputHelper output) :
    IClassFixture<AuthorizationDatabase>
{
    [Fact]
    [Trait("Category", "FrontendBrowser")]
    public async Task Physical_connection_loss_and_api_restart_recover_every_committed_event_in_all_browsers()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("WF_LIVE_RESYNC_BROWSER") == "true",
            "Set WF_LIVE_RESYNC_BROWSER=true and WF_FRONTEND_ROOT; install frontend dependencies and all Playwright engines.");
        await using var fixture = await Fixture.Start(database.DataSource);
        await RunBrowser(fixture, output);
    }

    internal static async Task RunBrowser(Fixture fixture, ITestOutputHelper output, bool drain = false)
    {
        var frontend = Environment.GetEnvironmentVariable("WF_FRONTEND_ROOT");
        Assert.True(frontend is not null && File.Exists(Path.Combine(frontend, "scripts", "live-resync-browser.ts")),
            "WF_FRONTEND_ROOT must identify the repository frontend directory.");
        var start = new ProcessStartInfo("bun")
        {
            WorkingDirectory = frontend, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(Path.Combine("scripts", "live-resync-browser.ts"));
        start.Environment["WF_RESYNC_API"] = fixture.Api;
        start.Environment["WF_RESYNC_CONTROL"] = fixture.Control;
        start.Environment["WF_RESYNC_TOKEN"] = fixture.Jwt;
        start.Environment["WF_RESYNC_RUN"] = fixture.RunId.ToString("D");
        start.Environment["WF_RESYNC_DRAIN"] = drain ? "true" : "false";
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromMinutes(4));
            await process.WaitForExitAsync(deadline.Token);
            output.WriteLine(await stdout);
            Assert.True(process.ExitCode == 0, $"Physical live resync browsers failed.\n{await stdout}\n{await stderr}");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
        }
    }

    // Only the test control plane and durable writer survive API disposal.
    internal sealed class Fixture(NpgsqlDataSource source, bool enableDrain) : IAsyncDisposable
    {
        private const string Tenant = "ef8a05a0-451c-4479-9c8d-391b6bc7ee1a";
        private const string Audience = "562b0870-4522-43da-8587-41e9b788f309";
        private const string Issuer = $"https://login.microsoftonline.com/{Tenant}/v2.0";
        private readonly RSA rsa = RSA.Create(2048);
        private readonly Guid group = Guid.NewGuid();
        private readonly SemaphoreSlim writer = new(1);
        private readonly ConcurrentDictionary<string, HttpContext> sockets = new();
        private readonly GroupCommitAppender appender = new(source, NullLogger<GroupCommitAppender>.Instance);
        private readonly RunState run = new(Guid.CreateVersion7(),
            [new NodeDefinition("node", [], [new TaskDefinition("first"), new TaskDefinition("second")])],
            new WorkflowMetadata("release", ImmutableSortedDictionary<string, string>.Empty), _ => { });
        private WebApplication? api;
        private WebApplication? control;
        private TaskCompletionSource? continueWriter;
        private bool offline;
        private int generation;
        private readonly TaskCompletionSource requestEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource releaseRequest = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task RequestEntered => requestEntered.Task;
        public void ReleaseRequest() => releaseRequest.TrySetResult();
        public string Api { get; private set; } = "http://127.0.0.1:0";
        public string Control { get; private set; } = "";
        public string Jwt { get; private set; } = "";
        public Guid RunId => run.Snapshot.RunId;

        public static async Task<Fixture> Start(NpgsqlDataSource source, bool enableDrain = false)
        {
            var fixture = new Fixture(source, enableDrain);
            try
            {
                await fixture.Initialize();
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        private async Task Initialize()
        {
            var token = TestContext.Current.CancellationToken;
            var team = "resync-" + Guid.NewGuid().ToString("N");
            await using (var connection = await source.OpenConnectionAsync(token))
            await using (var transaction = await connection.BeginTransactionAsync(token))
            {
                await new RegistryWriter().UpsertAsync(connection, transaction,
                    new TeamRegistry(team, [new(team, "release", "Live resync fixture", new([]),
                        [new(RegistryRole.Viewer, group)])]), token);
                await transaction.CommitAsync(token);
            }
            await new RunStore(source, NullLogger<RunStore>.Instance).CreateAsync(run, team + "/release/workflow",
                Guid.CreateVersion7(), DateTimeOffset.UtcNow.AddHours(1), token);
            Jwt = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(Issuer, Audience,
                [new Claim("tid", Tenant), new("ver", "2.0"), new("oid", Guid.NewGuid().ToString()),
                 new("azp", Guid.NewGuid().ToString()), new("groups", group.ToString()),
                 new("idtyp", "user"), new("scp", "api.read")],
                DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(10),
                new SigningCredentials(Key(), SecurityAlgorithms.RsaSha256)));
            await StartApi();
            control = Build("http://127.0.0.1:0", live: false);
            control.MapPost("/fixture/drop", () =>
            {
                Volatile.Write(ref offline, true);
                return Results.Ok(new { aborted = AbortSockets(), generation });
            }).RequireAuthorization();
            control.MapPost("/fixture/resume", () =>
            {
                Volatile.Write(ref offline, false);
                return Results.Ok(new { generation });
            }).RequireAuthorization();
            control.MapPost("/fixture/stop", () => StopApi()).RequireAuthorization();
            if (enableDrain)
                control.MapPost("/fixture/drain", () => StopApi(graceful: true)).RequireAuthorization();
            control.MapPost("/fixture/start", async () =>
            {
                await StartApi();
                Volatile.Write(ref offline, false);
                return Results.Ok(new { generation, api = Api });
            }).RequireAuthorization();
            control.MapPost("/fixture/append/{count:int}", async (int count, HttpContext context) =>
            {
                if (count is < 1 or > 64) return Results.BadRequest();
                var delay = context.Request.Query["delay"].ToString();
                if (!int.TryParse(delay, out var milliseconds) || milliseconds is < 0 or > 100)
                    return Results.BadRequest();
                if (!int.TryParse(context.Request.Query["pauseAfter"], out var pauseAfter) ||
                    pauseAfter < 0 || pauseAfter >= count)
                    return Results.BadRequest();
                await Append(count, milliseconds, pauseAfter, context.RequestAborted);
                return Results.Ok(new { lastSeq = run.Snapshot.LastSequence, apiRunning = api is not null });
            }).RequireAuthorization();
            control.MapPost("/fixture/continue", () =>
            {
                var pending = Volatile.Read(ref continueWriter);
                return pending is not null && pending.TrySetResult()
                    ? (IResult)Results.Ok(new { resumed = true }) : Results.Conflict();
            }).RequireAuthorization();
            control.MapGet("/fixture/audit", (Delegate)Audit).RequireAuthorization();
            await control.StartAsync(token);
            Control = Address(control);
        }

        private RsaSecurityKey Key() => new(rsa) { KeyId = "physical-live-resync" };

        private WebApplication Build(string address, bool live = true)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls(address);
            builder.Configuration["AzureAd:TenantId"] = Tenant;
            builder.Configuration["AzureAd:ClientId"] = Audience;
            builder.Services.AddWorkflowAuthentication(builder.Configuration, builder.Environment);
            builder.Services.AddTeamAuthorization();
            builder.Services.AddSingleton(source);
            builder.Services.ConfigureHttpJsonOptions(options =>
                options.SerializerOptions.TypeInfoResolverChain.Insert(0, RunEventsJsonContext.Default));
            builder.Services.AddSingleton(TimeProvider.System);
            if (live) builder.Services.AddWorkflowLive();
            if (live && enableDrain) builder.Services.AddWorkflowDrain(builder.Configuration);
            var metadata = new OpenIdConnectConfiguration { Issuer = Issuer };
            metadata.SigningKeys.Add(Key());
            var manager = new StaticConfigurationManager<OpenIdConnectConfiguration>(metadata);
            builder.Services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.Configuration = metadata;
                options.ConfigurationManager = manager;
                options.TokenValidationParameters.IssuerValidator =
                    AadIssuerValidator.GetAadIssuerValidator(Issuer, null, _ => manager).Validate;
                options.TokenValidationParameters.ClockSkew = TimeSpan.Zero;
            });
            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            return app;
        }

        public async Task StartApi()
        {
            Assert.Null(api);
            var app = Build(Api);
            app.Use(async (context, next) =>
            {
                if (context.Request.Path != LiveHub.Path)
                {
                    await next(context);
                    return;
                }
                if (Volatile.Read(ref offline))
                {
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    return;
                }
                sockets[context.Connection.Id] = context;
                try { await next(context); }
                finally { sockets.TryRemove(context.Connection.Id, out _); }
            });
            app.MapRunEventsEndpoints();
            app.MapHub<LiveHub>(LiveHub.Path, options => options.CloseOnAuthenticationExpiration = true);
            if (enableDrain)
            {
                app.MapWorkflowReadiness();
                app.MapGet("/fixture/held", async (HttpContext context) =>
                {
                    requestEntered.TrySetResult();
                    await releaseRequest.Task.WaitAsync(context.RequestAborted);
                    return Results.Ok(new { finished = true });
                }).RequireAuthorization();
            }
            try
            {
                await app.StartAsync(TestContext.Current.CancellationToken);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(10));
                while (!app.Services.GetRequiredService<LiveFanout>().IsReady) await Task.Delay(10, deadline.Token);
                Api = Address(app);
                api = app;
                generation++;
                Volatile.Write(ref offline, false);
            }
            catch
            {
                await app.DisposeAsync();
                throw;
            }
        }

        private int AbortSockets()
        {
            var active = sockets.Values.ToArray();
            foreach (var context in active) context.Abort();
            return active.Length;
        }

        public async Task<IResult> StopApi(bool graceful = false)
        {
            Volatile.Write(ref offline, true);
            var aborted = graceful ? sockets.Count : AbortSockets();
            var previous = api ?? throw new InvalidOperationException("API already stopped.");
            api = null;
            if (graceful) previous.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await previous.StopAsync(deadline.Token);
            await previous.DisposeAsync();
            using var probe = new TcpClient();
            await Assert.ThrowsAsync<SocketException>(() => probe.ConnectAsync("127.0.0.1", new Uri(Api).Port));
            return Results.Ok(new { aborted, generation, portClosed = true });
        }

        public async Task Append(int count, int delay, int pauseAfter, CancellationToken token)
        {
            await writer.WaitAsync(token);
            try
            {
                var continuation = pauseAfter == 0 ? null :
                    new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Volatile.Write(ref continueWriter, continuation);
                for (var index = 0; index < count; index++)
                {
                    var preview = RunState.Restore(run.Snapshot, [], _ => { });
                    RunEvent change;
                    if (preview.Snapshot.LastSequence == 0)
                        change = preview.Append(EventEntity.Run, null, (int)RunStatus.Running, DateTimeOffset.UtcNow);
                    else if (preview.Snapshot.Nodes["node"] == ExecutionStatus.Pending)
                        change = preview.Append(EventEntity.Node, "node", (int)ExecutionStatus.Ready, DateTimeOffset.UtcNow);
                    else
                    {
                        var key = preview.Snapshot.LastSequence % 2 == 0 ? "node/first" : "node/second";
                        var next = preview.Snapshot.Tasks[key] switch
                        {
                            ExecutionStatus.Pending => ExecutionStatus.Ready,
                            ExecutionStatus.Ready or ExecutionStatus.Retrying => ExecutionStatus.Dispatched,
                            ExecutionStatus.Dispatched => ExecutionStatus.Running,
                            ExecutionStatus.Running => ExecutionStatus.Retrying,
                            _ => throw new InvalidOperationException("Unexpected resync fixture task state.")
                        };
                        change = preview.Append(EventEntity.Task, key, (int)next, DateTimeOffset.UtcNow);
                    }
                    await appender.AppendAsync(change, token);
                    run.Apply(change);
                    // A bounded barrier proves writes straddle recovery without depending on reconnect timing.
                    if (index + 1 == pauseAfter)
                        await continuation!.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
                    if (delay > 0) await Task.Delay(delay, token);
                }
            }
            finally
            {
                Volatile.Write(ref continueWriter, null);
                writer.Release();
            }
        }

        private async Task<IResult> Audit(HttpContext context)
        {
            await using var command = source.CreateCommand("""
                SELECT seq,payload FROM run_events
                WHERE run_day=uuid_v7_run_day($1) AND run_id=$1 ORDER BY seq
                """);
            command.Parameters.AddWithValue(RunId);
            await using var reader = await command.ExecuteReaderAsync(context.RequestAborted);
            var events = new List<LiveEvent>();
            while (await reader.ReadAsync(context.RequestAborted))
            {
                var change = JsonSerializer.Deserialize(reader.GetFieldValue<byte[]>(1), LiveResyncJson.Default.RunEvent)!;
                Assert.Equal(events.Count + 1L, reader.GetInt64(0));
                Assert.Equal(reader.GetInt64(0), change.Sequence);
                events.Add(LiveEvent.From(change));
            }

            return Results.Ok(new { events, generation, apiRunning = api is not null });
        }

        private static string Address(WebApplication app) =>
            app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();

        public async ValueTask DisposeAsync()
        {
            releaseRequest.TrySetResult();
            Volatile.Write(ref offline, true);
            AbortSockets();
            if (control is not null) await control.DisposeAsync();
            if (api is not null) await api.DisposeAsync();
            await appender.DisposeAsync();
            writer.Dispose();
            rsa.Dispose();
        }
    }
}

[JsonSerializable(typeof(RunEvent))]
internal sealed partial class LiveResyncJson : JsonSerializerContext;
