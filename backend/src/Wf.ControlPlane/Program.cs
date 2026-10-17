using System.Net;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Npgsql;
using Wf.ControlPlane.Api;
using Wf.ControlPlane.Auth;
using Wf.ControlPlane.Data;
using Wf.ControlPlane.Engine.Agents;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.ControlPlane.Engine.Events;
using Wf.ControlPlane.Engine.Leadership;
using Wf.ControlPlane.Engine.Recovery;
using Wf.ControlPlane.Engine.Registry;
using Wf.ControlPlane.Engine.Scheduler;
using Wf.ControlPlane.Engine.Sweeper;
using Wf.ControlPlane.Hosting;
using Wf.ControlPlane.Live;
using Wf.ControlPlane.Registry;
using Wf.ControlPlane.Registration;
using Wf.Contracts.Reports;
using Wf.Engine.Core.Placement;

if (args.Count(argument => argument == "--migrate") > 1)
    throw new ArgumentException("--migrate cannot be repeated.", nameof(args));
if (args.Count(argument => argument == "--dev") > 1)
    throw new ArgumentException("--dev cannot be repeated.", nameof(args));
if (args.Count(argument => argument == "--lan") > 1)
    throw new ArgumentException("--lan cannot be repeated.", nameof(args));
var migrate = args.Contains("--migrate", StringComparer.Ordinal);
var developmentAuth = args.Contains("--dev", StringComparer.Ordinal);
var builder = WebApplication.CreateBuilder(args.Where(argument => argument is not ("--migrate" or "--dev" or "--lan")).ToArray());
if (args.Contains("--lan", StringComparer.Ordinal)) builder.Configuration["Lan:Enabled"] = "true";
var compose = LocalComposeOptions.Read(builder.Configuration, builder.Environment);
using var lan = LanListener.Read(builder.Configuration, builder.Environment);
var engineEnabled = builder.Configuration.GetValue("Engine:Enabled", true);
// Upload paths and live query strings carry bearer capabilities; suppress raw framework request/route logs.
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
var port = builder.Configuration.GetValue("Engine:Port", 5000);
if (port is < 1 or > 65535) throw new InvalidOperationException("Engine:Port must be between 1 and 65535.");
var apiPort = builder.Configuration.GetValue("Api:Port", 5001);
if (apiPort is < 1 or > 65535 || apiPort == port)
    throw new InvalidOperationException("Api:Port must be between 1 and 65535 and differ from Engine:Port.");
builder.WebHost.ConfigureKestrel(options =>
{
    var address = compose is null ? IPAddress.Loopback : IPAddress.Any;
    options.Listen(address, port, listen => listen.Protocols = HttpProtocols.Http2);
    options.Listen(address, apiPort, listen => listen.Protocols = HttpProtocols.Http1);
    lan?.Configure(options);
});
builder.Services.AddGrpc(options => options.MaxReceiveMessageSize = 1024 * 1024);
var connectionString = builder.Configuration.GetConnectionString("Workflow")
    ?? throw new InvalidOperationException("ConnectionStrings:Workflow is required; run --migrate before starting the host.");
if (migrate)
{
    using var logging = LoggerFactory.Create(options => options.AddConsole());
    Migrator.Upgrade(connectionString, logging.CreateLogger("Wf.Migrations"));
    return;
}
if (developmentAuth) builder.Configuration["Authentication:Mode"] = "Development";
builder.Services.AddWorkflowAuthentication(builder.Configuration, builder.Environment);
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
builder.Services.AddTeamAuthorization();
builder.Services.AddUserSchedules(builder.Configuration);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AbortRunJson.Default);
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, TaskLogsJsonContext.Default);
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, Wf.Contracts.Outputs.TaskOutputsJsonContext.Default);
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, VersionsJsonContext.Default);
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, VariablesJsonContext.Default);
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, Wf.Contracts.Runs.RunJsonContext.Default);
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, RunEventsJsonContext.Default);
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, RunQueryJsonContext.Default);
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, ReservationQueueJsonContext.Default);
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, Wf.ControlPlane.Api.Machines.MachineJsonContext.Default);
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, Wf.ControlPlane.Api.Machines.CalibrationJsonContext.Default);
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, RunHistoryJsonContext.Default);
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AuditJsonContext.Default);
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, MetricsJsonContext.Default);
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, RunTimelineJsonContext.Default);
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, Wf.ControlPlane.Api.Series.SeriesTrendsJson.Default);
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, Wf.ControlPlane.Api.Dashboards.DashboardJson.Default);
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, ReportIndexJsonContext.Default);
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, ReportCompareJsonContext.Default);
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, Wf.Contracts.Resources.ResourceJsonContext.Default);
});
builder.Services.AddWorkflowOpenApi();
builder.Services.AddWorkflowLive();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddWorkflowDrain(builder.Configuration, engineEnabled);
builder.Services.AddWorkflowRegistrations(builder.Configuration);
builder.Services.AddSingleton<ActiveVersionCache>();
builder.Services.AddHostedService<ActiveVersionCache>(services => services.GetRequiredService<ActiveVersionCache>());
builder.Services.AddSingleton(new LeaseOptions(Guid.NewGuid().ToString("N"),
    new Uri(builder.Configuration["Engine:PublicEndpoint"] ?? lan?.Endpoint.AbsoluteUri ?? $"http://localhost:{port}", UriKind.Absolute)));
builder.Services.AddSingleton<LeaseManager>();
if (engineEnabled)
    builder.Services.AddHostedService<LeaseManager>(services => services.GetRequiredService<LeaseManager>());
builder.Services.Configure<HostOptions>(options => options.ServicesStopConcurrently = false);
if (engineEnabled) builder.Services.AddHostedService<EngineShutdownService>();
builder.Services.AddSingleton<AgentRegistry>();
builder.Services.AddSingleton<RunStore>();
builder.Services.AddSingleton<CalibrationService>();
builder.Services.AddSingleton<CommandInbox>();
builder.Services.AddSingleton<RunRecovery>();
if (engineEnabled)
    builder.Services.AddHostedService<RunRecovery>(services => services.GetRequiredService<RunRecovery>());
builder.Services.AddSingleton(services => new ScheduleCatchUp(services.GetRequiredService<TimeProvider>(),
    builder.Configuration.GetValue<TimeSpan?>("Scheduler:FireGrace")));
builder.Services.AddSingleton<ScheduleFireService>();
if (engineEnabled)
    builder.Services.AddHostedService<ScheduleFireService>(services => services.GetRequiredService<ScheduleFireService>());
builder.Services.AddSingleton<GroupCommitAppender>();
builder.Services.AddSingleton<PlacementEngine>();
builder.Services.AddSingleton<Dispatcher>();
builder.Services.AddSingleton<Wf.ControlPlane.Engine.Reservations.ReservationManager>();
if (builder.Configuration.GetValue("Engine:ExecuteRuns", false))
{
    if (!engineEnabled) throw new InvalidOperationException("Engine:ExecuteRuns requires Engine:Enabled.");
    var endpoint = new Uri(builder.Configuration["Engine:ApiEndpoint"] ?? lan?.Endpoint.AbsoluteUri ??
        $"http://localhost:{apiPort}", UriKind.Absolute);
    if (endpoint.Scheme is not ("https" or "http") ||
        endpoint.Scheme == "http" && !endpoint.IsLoopback && !(compose is not null && LocalComposeOptions.IsPackageOrigin(endpoint)) ||
        endpoint.UserInfo.Length != 0 || endpoint.AbsolutePath != "/" || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
        throw new InvalidOperationException("Engine:ApiEndpoint must be an HTTPS origin or loopback HTTP origin.");
    builder.Services.AddSingleton(new RunExecutionOptions(endpoint));
    builder.Services.AddHostedService<RunExecutionService>();
}
if (engineEnabled) builder.Services.AddHostedService<Wf.ControlPlane.Engine.Reservations.ReservationQueueService>();
builder.Services.AddSingleton<Wf.ControlPlane.Engine.Reservations.ReservationLeaseService>();
if (engineEnabled)
    builder.Services.AddHostedService<Wf.ControlPlane.Engine.Reservations.ReservationLeaseService>(
        services => services.GetRequiredService<Wf.ControlPlane.Engine.Reservations.ReservationLeaseService>());
if (engineEnabled) builder.Services.AddHostedService<AgentRegistryService>();
builder.Services.AddSingleton<AgentLossSweeper>();
if (engineEnabled)
    builder.Services.AddHostedService<AgentLossSweeper>(services => services.GetRequiredService<AgentLossSweeper>());
builder.Services.AddSingleton<AgentLossHandler>();
if (engineEnabled)
    builder.Services.AddHostedService<AgentLossHandler>(services => services.GetRequiredService<AgentLossHandler>());
var app = builder.Build();
if (lan is not null)
    app.Logger.LogWarning("Trusted local LAN profile: {Endpoint}. Development authentication grants LAN clients full access; use only on a trusted network.",
        lan.Endpoint);
if (compose is not null)
    app.Logger.LogWarning("Local Compose {Environment}: login-free Development access is restricted to the dedicated {Network} container network. Publish only on the configured host loopback.",
        compose.Environment, compose.Network);
app.UseReportResponseHeaders();
app.UseAuthentication();
app.UseAuthorization();
if (engineEnabled) app.MapGrpcService<AgentGatewayService>().RequireAuthorization(AuthExtensions.Agent);
app.MapRegistryEndpoints();
app.MapCatalogEndpoints();
app.MapVersionsEndpoints();
app.MapVariablesEndpoints();
app.MapRegistrationEndpoints();
app.MapRunsEndpoints();
app.MapRunEventsEndpoints();
app.MapRunQueryEndpoints();
Wf.ControlPlane.Api.Machines.MachineEndpoints.MapMachineEndpoints(app);
app.MapRunHistoryEndpoints();
app.MapAuditEndpoints();
app.MapMetricsEndpoints();
app.MapRunTimelineEndpoints();
app.MapTaskLogsEndpoints();
app.MapReportContentEndpoints();
app.MapReportIndexEndpoints();
app.MapReportCompareEndpoints();
app.MapSchedulesEndpoints();
app.MapSeriesFiresEndpoints();
Wf.ControlPlane.Api.Series.TrendsEndpoint.MapTrendsEndpoint(app);
Wf.ControlPlane.Api.Dashboards.DashboardEndpoints.MapDashboardEndpoints(app);
app.MapUserSchedulesEndpoints();
app.MapPermissionsEndpoints();
app.MapWorkflowOpenApi();
app.MapHub<LiveHub>(LiveHub.Path, options => options.CloseOnAuthenticationExpiration = true);
app.MapGet("/healthz", () => Results.Ok()).AllowAnonymous();
app.MapWorkflowReadiness();
app.MapGet("/healthz/leader", (LeaseManager leadership, RunRecovery recovery, ActiveVersionCache versions, ScheduleFireService schedules) =>
    leadership.IsLeader && recovery.IsReady && versions.IsReady && schedules.IsReady
        ? Results.Ok() : Results.StatusCode(StatusCodes.Status503ServiceUnavailable)).AllowAnonymous();
await app.RunAsync();
