using Wf.AppHost;

var builder = DistributedApplication.CreateBuilder(AgentResources.NormalizeArguments(args));
LocalStack.Configure(builder);
await builder.Build().RunAsync();

public partial class Program;
