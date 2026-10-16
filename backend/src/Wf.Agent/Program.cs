using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wf.Agent;
using Wf.Agent.Hosts;
using Wf.Agent.Packages;
using Wf.Agent.Journal;
using Wf.Agent.Logs;
using Wf.Agent.Sampling;
using Wf.Agent.Secrets;
using Wf.Contracts.Definition;

var provisioning = args.Length == 2 && args[0] == "--set-secret";
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{ Args = provisioning ? [] : args, ContentRootPath = AppContext.BaseDirectory });
var secretStore = SecretStoreConfiguration.Create(builder.Configuration);
if (provisioning)
{
    try
    {
        if (secretStore is not LocalSecretStore local || !Console.IsInputRedirected)
            throw new ArgumentException("Local secret provisioning requires redirected standard input.");
        using var input = Console.OpenStandardInput();
        var bytes = new byte[LocalSecretStore.MaxSecretBytes + 3];
        try
        {
            var count = 0;
            while (count < bytes.Length)
            {
                var read = await input.ReadAsync(bytes.AsMemory(count));
                if (read == 0) break;
                count += read;
            }
            if (count == bytes.Length) throw new ArgumentException("Secret exceeds the supported size.");
            var value = new System.Text.UTF8Encoding(false, true).GetString(bytes, 0, count);
            if (value.EndsWith("\r\n", StringComparison.Ordinal)) value = value[..^2];
            else if (value.EndsWith('\n')) value = value[..^1];
            await local.WriteAsync(new SecretReference(args[1]), value);
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
    }
    catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
    {
        Console.Error.WriteLine("Secret provisioning failed; check reference, provider and private storage permissions.");
        Environment.ExitCode = 1;
    }
    return;
}
builder.Services.AddSingleton(secretStore);
builder.Services.AddWindowsService(options => options.ServiceName = builder.Configuration["Agent:ServiceName"] ?? "wf-agent");
builder.Services.AddSystemd();
builder.Services.AddSingleton(AgentOptions.FromConfiguration(builder.Configuration));
var insecurePackageOrigin = builder.Configuration["Agent:InsecurePackageOrigin"] is { } origin
    ? new Uri(origin, UriKind.Absolute) : null;
if (insecurePackageOrigin is not null && !builder.Configuration.GetValue("Agent:AllowInsecureTransport", false))
    throw new InvalidOperationException("Agent:InsecurePackageOrigin also requires the explicit isolated-network AllowInsecureTransport opt-in.");
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(provider =>
{
    var options = provider.GetRequiredService<AgentOptions>();
    return new ResultJournal(options.ResultJournalDirectory, options.Id);
});
builder.Services.AddSingleton<AgentOutbox>();
builder.Services.AddSingleton(NativeSamplingOptions.FromConfiguration(builder.Configuration));
builder.Services.AddSingleton(provider => ResourceSampler.Create(provider.GetRequiredService<NativeSamplingOptions>()));
builder.Services.AddSingleton<ResourceSamplingService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<ResourceSamplingService>());
builder.Services.AddSingleton<TaskHostPool>();
builder.Services.AddSingleton<AgentLogService>();
builder.Services.AddSingleton(provider => new PackageCache(
    Path.GetFullPath(builder.Configuration["Agent:PackageCache"] ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "wf-agent", "packages")),
    provider.GetRequiredService<ILogger<PackageCache>>(), insecureOrigin: insecurePackageOrigin));
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
