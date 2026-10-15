using System.Net;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Npgsql;
using Wf.ControlPlane.Engine.Agents;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 5000,
    listen => listen.Protocols = HttpProtocols.Http2));
builder.Services.AddGrpc(options => options.MaxReceiveMessageSize = 1024 * 1024);
var connectionString = builder.Configuration.GetConnectionString("Workflow")
    ?? throw new InvalidOperationException("ConnectionStrings:Workflow is required; apply backend/db/migrations before starting.");
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<AgentRegistry>();
builder.Services.AddHostedService<AgentRegistryService>();
var app = builder.Build();
app.MapGrpcService<AgentGatewayService>();
app.MapGet("/healthz", () => Results.Ok());
await app.RunAsync();
