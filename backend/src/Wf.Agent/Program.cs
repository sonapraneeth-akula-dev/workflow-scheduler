using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wf.Agent;
using Wf.Contracts.Agent.V1;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.Services.AddWindowsService(options => options.ServiceName = builder.Configuration["Agent:ServiceName"] ?? "wf-agent");
builder.Services.AddSystemd();
builder.Services.AddSingleton(AgentOptions.FromConfiguration(builder.Configuration));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IAgentTelemetry, AgentTelemetry>();
builder.Services.AddSingleton<IAgentCommandHandler, UnsupportedCommands>();
builder.Services.AddSingleton<AgentConnection>();
builder.Services.AddHostedService<AgentWorker>();
await builder.Build().RunAsync();

internal sealed class AgentWorker(AgentConnection connection, ILogger<AgentWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await connection.RunAsync(stoppingToken); }
        catch (Exception exception)
        {
            logger.LogCritical(exception, "Agent stopped after an unrecoverable failure.");
            Environment.ExitCode = 1;
            throw;
        }
    }
}

internal sealed class UnsupportedCommands : IAgentCommandHandler
{
    public ValueTask HandleAsync(ControlMessage message, CancellationToken cancellationToken) =>
        throw new NotSupportedException($"Agent command '{message.PayloadCase}' requires its task-host/drain implementation.");
}
