using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wf.Agent;
using Wf.Agent.Hosts;
using Wf.Agent.Packages;
using Wf.Agent.Journal;
using Wf.Agent.Logs;
using Wf.Agent.Sampling;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.Services.AddWindowsService(options => options.ServiceName = builder.Configuration["Agent:ServiceName"] ?? "wf-agent");
builder.Services.AddSystemd();
builder.Services.AddSingleton(AgentOptions.FromConfiguration(builder.Configuration));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(provider =>
{
    var options = provider.GetRequiredService<AgentOptions>();
    return new ResultJournal(options.ResultJournalDirectory, options.Id);
});
builder.Services.AddSingleton<AgentOutbox>();
builder.Services.AddSingleton(_ => ResourceSampler.Create());
builder.Services.AddSingleton<ResourceSamplingService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<ResourceSamplingService>());
builder.Services.AddSingleton<TaskHostPool>();
builder.Services.AddSingleton<AgentLogService>();
builder.Services.AddSingleton(provider => new PackageCache(
    Path.GetFullPath(builder.Configuration["Agent:PackageCache"] ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "wf-agent", "packages")),
    provider.GetRequiredService<ILogger<PackageCache>>()));
builder.Services.AddSingleton<TaskCommands>();
builder.Services.AddSingleton<IAgentTelemetry, AgentTelemetry>();
builder.Services.AddSingleton<IAgentCommandHandler>(provider => provider.GetRequiredService<TaskCommands>());
builder.Services.AddSingleton<AgentConnection>();
builder.Services.AddHostedService<AgentWorker>();
await builder.Build().RunAsync();

internal sealed class AgentWorker(AgentConnection connection, AgentLogService logs, ILogger<AgentWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await logs.RecoverAsync(stoppingToken);
            await connection.RunAsync(stoppingToken);
        }
        catch (Exception exception)
        {
            logger.LogCritical(exception, "Agent stopped after an unrecoverable failure.");
            Environment.ExitCode = 1;
            throw;
        }
    }
}
