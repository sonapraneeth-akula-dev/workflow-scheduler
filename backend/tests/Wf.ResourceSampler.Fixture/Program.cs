using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using Wf.Agent.ResourceFixture;
using Wf.Sdk;

if (args is ["--network-worker", var json])
{
    await Traffic.RunAsync(JsonSerializer.Deserialize<TrafficRequest>(json)!, CancellationToken.None);
    return 0;
}

var workflow = Workflow.Define("tests/resources/fixture", "1.0.0", builder => builder
    .Type("test")
    .Node("source", node => node.Task("noop", _ => { }))
    .Node("work", node => node.DependsOn("source").Input("source", "value")
        .Task("echo", context =>
        {
            context.Outputs["pid"] = Environment.ProcessId;
            var rights = Traffic.Rights();
            context.Outputs["capabilities"] = rights.Capabilities;
            context.Outputs["noNewPrivileges"] = rights.NoNewPrivileges;
        })
        .Task("traffic", async context =>
        {
            var request = JsonSerializer.Deserialize<TrafficRequest>(context.Input<string>("source", "value")!)!;
            await Traffic.RunAsync(request, context.CancellationToken);
        })));
return await workflow.RunAsync(args);

namespace Wf.Agent.ResourceFixture
{
    public sealed class ResourceFixtureMarker;
    public sealed record TrafficRequest(string Ready, string Go, string Done, string Stop,
        bool Client, bool Ipv6, int Count, int SendBytes, int ReceiveBytes, bool Child,
        string RuntimeConfig, string DepsFile);
    public sealed record TrafficReady(int ProcessId, int Port, string? Capabilities, bool NoNewPrivileges);

    internal static class Traffic
    {
        public static async Task RunAsync(TrafficRequest request, CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(40));
            if (request.Child)
            {
                var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
                foreach (var argument in new[] { "exec", "--runtimeconfig", request.RuntimeConfig, "--depsfile", request.DepsFile,
                             Assembly.GetExecutingAssembly().Location, "--network-worker", JsonSerializer.Serialize(request with { Child = false }) })
                    info.ArgumentList.Add(argument);
                using var child = Process.Start(info)!;
                try { await child.WaitForExitAsync(timeout.Token); }
                finally
                {
                    if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(CancellationToken.None); }
                }
                if (child.ExitCode != 0) throw new InvalidOperationException($"Owned network child failed ({child.ExitCode}).");
                return;
            }
            var address = request.Ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback;
            using var socket = new Socket(request.Ipv6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork,
                SocketType.Dgram, ProtocolType.Udp);
            if (request.Ipv6) socket.DualMode = false;
            socket.Bind(new IPEndPoint(address, 0));
            var rights = Rights();
            await WriteAsync(request.Ready, JsonSerializer.Serialize(new TrafficReady(Environment.ProcessId,
                ((IPEndPoint)socket.LocalEndPoint!).Port, rights.Capabilities, rights.NoNewPrivileges)), timeout.Token);
            await WaitAsync(request.Go, timeout.Token);
            var port = int.Parse(await File.ReadAllTextAsync(request.Go, timeout.Token), CultureInfo.InvariantCulture);
            socket.Connect(new IPEndPoint(address, port));
            await WriteAsync(request.Ready + ".connected", "1", timeout.Token);
            await WaitAsync(request.Go + ".connected", timeout.Token);
            var send = new byte[request.SendBytes];
            var receive = new byte[request.ReceiveBytes + 1];
            for (var index = 0; index < request.Count; index++)
            {
                if (request.Client) await SendAsync();
                var count = await socket.ReceiveAsync(receive, SocketFlags.None, timeout.Token);
                if (count != request.ReceiveBytes) throw new InvalidDataException("Owned UDP datagram length mismatch.");
                if (!request.Client) await SendAsync();
            }
            await WriteAsync(request.Done, "complete", timeout.Token);
            await WaitAsync(request.Stop, timeout.Token);

            async Task SendAsync()
            {
                if (await socket.SendAsync(send, SocketFlags.None, timeout.Token) != send.Length)
                    throw new IOException("Owned UDP datagram was not sent completely.");
            }
        }

        public static (string? Capabilities, bool NoNewPrivileges) Rights()
        {
            if (!OperatingSystem.IsLinux()) return (null, false);
            var values = File.ReadLines("/proc/self/status").Where(line =>
                    line.StartsWith("CapEff:", StringComparison.Ordinal) || line.StartsWith("NoNewPrivs:", StringComparison.Ordinal))
                .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                .ToDictionary(parts => parts[0], parts => parts[1]);
            return (values["CapEff:"], values["NoNewPrivs:"] == "1");
        }

        private static async Task WaitAsync(string path, CancellationToken token)
        {
            while (!File.Exists(path)) await Task.Delay(10, token);
        }

        private static async Task WriteAsync(string path, string content, CancellationToken token)
        {
            await File.WriteAllTextAsync(path + ".new", content, token);
            File.Move(path + ".new", path);
        }
    }
}
