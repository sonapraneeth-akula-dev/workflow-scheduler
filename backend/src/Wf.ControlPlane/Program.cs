using System.Net;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Npgsql;
using Wf.ControlPlane.Engine.Agents;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Events;
using Wf.ControlPlane.Engine.Leadership;
using Wf.ControlPlane.Engine.Recovery;
using Wf.ControlPlane.Engine.Sweeper;
using Wf.Engine.Core.Placement;

var builder = WebApplication.CreateBuilder(args);
var port = builder.Configuration.GetValue("Engine:Port", 5000);
if (port is < 1 or > 65535) throw new InvalidOperationException("Engine:Port must be between 1 and 65535.");
builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port,
    listen => listen.Protocols = HttpProtocols.Http2));
builder.Services.AddGrpc(options => options.MaxReceiveMessageSize = 1024 * 1024);
var connectionString = builder.Configuration.GetConnectionString("Workflow")
    ?? throw new InvalidOperationException("ConnectionStrings:Workflow is required; apply backend/db/migrations before starting.");
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(new LeaseOptions(Guid.NewGuid().ToString("N"),
    new Uri(builder.Configuration["Engine:PublicEndpoint"] ?? $"http://localhost:{port}", UriKind.Absolute)));
builder.Services.AddSingleton<LeaseManager>();
builder.Services.AddHostedService<LeaseManager>(services => services.GetRequiredService<LeaseManager>());
builder.Services.AddSingleton<AgentRegistry>();
builder.Services.AddSingleton<RunStore>();
builder.Services.AddSingleton<CommandInbox>();
builder.Services.AddSingleton<RunRecovery>();
builder.Services.AddHostedService<RunRecovery>(services => services.GetRequiredService<RunRecovery>());
builder.Services.AddSingleton<GroupCommitAppender>();
builder.Services.AddSingleton<PlacementEngine>();
builder.Services.AddSingleton<Dispatcher>();
builder.Services.AddHostedService<AgentRegistryService>();
builder.Services.AddSingleton<AgentLossSweeper>();
builder.Services.AddHostedService<AgentLossSweeper>(services => services.GetRequiredService<AgentLossSweeper>());
builder.Services.AddSingleton<AgentLossHandler>();
builder.Services.AddHostedService<AgentLossHandler>(services => services.GetRequiredService<AgentLossHandler>());
var app = builder.Build();
app.MapGrpcService<AgentGatewayService>();
app.MapGet("/healthz", () => Results.Ok());
app.MapGet("/healthz/leader", (LeaseManager leadership, RunRecovery recovery) =>
    leadership.IsLeader && recovery.IsReady ? Results.Ok() : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
await app.RunAsync();
