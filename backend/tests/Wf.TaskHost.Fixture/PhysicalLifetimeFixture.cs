using System.Diagnostics;
using System.Globalization;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Wf.Contracts.Host.V1;
using Wf.Contracts.Definition;
using Wf.Sdk;

namespace Wf.TaskHost.Fixture;

public static class PhysicalLifetimeFixture
{
    public static async Task<int> RunProtocol2Async(string socketPath)
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions { Args = [] });
        builder.WebHost.UseKestrelCore().ConfigureKestrel(options =>
            options.ListenUnixSocket(socketPath, listen => listen.Protocols = HttpProtocols.Http2));
        builder.Services.AddRoutingCore();
        builder.Services.AddGrpc();
        builder.Services.AddSingleton<Protocol2Service>();
        await using var app = builder.Build();
        app.MapGrpcService<Protocol2Service>();
        _ = Task.Run(() => { Console.OpenStandardInput().ReadByte(); app.Lifetime.StopApplication(); });
        await app.RunAsync();
        return 0;
    }

    public sealed class Protocol2Service : Wf.Contracts.Host.V1.TaskHost.TaskHostBase
    {
        public override Task<HostReady> Ready(ReadyRequest request, ServerCallContext context) =>
            Task.FromResult(new HostReady { ProtocolVersion = 2, ProcessId = (uint)Environment.ProcessId, WorkflowVersion = "1.0.0" });
    }

    public static WorkflowDefinition Create() => Workflow.Define("tests/hosts/physical", "1.0.0", workflow =>
        workflow.Type("test").Variables(variables => variables.String("marker"))
            .Node("work", node => node
                .Task("complete", context => { Spawn(context.Var<string>("marker")); })
                .Task("exit", context =>
                {
                    Spawn(context.Var<string>("marker"));
                    Environment.Exit(0);
                })
                .Task("block", context =>
                {
                    Spawn(context.Var<string>("marker"));
                    Thread.Sleep(Timeout.Infinite);
                })
                .Task("cooperate", async context =>
                {
                    Spawn(context.Var<string>("marker"));
                    await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
                })));

    private static void Spawn(string marker)
    {
        File.WriteAllText(marker + ".worker", Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : "/usr/bin/setsid")
        { UseShellExecute = false };
        if (OperatingSystem.IsWindows())
        {
            start.ArgumentList.Add("/d");
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add("ping -n 120 127.0.0.1 >nul");
        }
        else
        {
            // Ordinary detached children defeat process-group-only cleanup; the SDK subreaper must adopt them.
            foreach (var value in new[] { "/bin/sh", "-c", "sleep 120 & wait" }) start.ArgumentList.Add(value);
        }
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Physical fixture child did not start.");
        File.WriteAllText(marker, child.Id.ToString(CultureInfo.InvariantCulture));
    }
}
