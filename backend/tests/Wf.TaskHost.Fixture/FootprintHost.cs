using Wf.Sdk;
using Wf.Sdk.Hosting;

namespace Wf.TaskHost.Fixture;

public static class FootprintHost
{
    private static byte[]? allocation;

    public static async Task RunAsync(string[] arguments)
    {
        if (Environment.GetEnvironmentVariable("WF_FOOTPRINT_PROCESS_MARKERS") is { } processMarkers)
            await WriteMarkerAsync(Path.Combine(processMarkers, $"process-{Environment.ProcessId}.pid"));
        string? Value(string key)
        {
            var index = Array.IndexOf(arguments, key);
            return index < 0 ? null : arguments[index + 1];
        }

        if (Value("--started-marker") is { } marker)
            await WriteMarkerAsync(marker);
        if (Value("--grow-trigger") is { } trigger)
        {
            _ = Task.Run(async () =>
            {
                while (!File.Exists(trigger)) await Task.Delay(25);
                var bytes = int.Parse(Value("--grow-mb")!, System.Globalization.CultureInfo.InvariantCulture) * 1024 * 1024;
                allocation = GC.AllocateUninitializedArray<byte>(bytes);
                for (var offset = 0; offset < allocation.Length; offset += 4096) allocation[offset] = 1;
                await File.WriteAllTextAsync(trigger + ".grown", "ready");
            });
        }
        if (Value("--ready-delay-ms") is { } delay)
            await Task.Delay(int.Parse(delay, System.Globalization.CultureInfo.InvariantCulture));
        var workflow = Workflow.Define("tests/footprint", Value("--version") ?? "1.0.0", w =>
            w.Type("test").Variables(v => v.String("marker", "")).Node("work",
                n => n.Task("echo", ctx => ctx.Outputs["pid"] = Environment.ProcessId)
                .Task("hold", async ctx =>
                {
                    if (ctx.Var<string>("marker") is { Length: > 0 } markerPath) await WriteMarkerAsync(markerPath);
                    await Task.Delay(Timeout.InfiniteTimeSpan, ctx.CancellationToken);
                })));
        await TaskHostServer.RunAsync(workflow, Value("--socket")!);
        GC.KeepAlive(allocation);
    }

    private static async Task WriteMarkerAsync(string path)
    {
        await File.WriteAllTextAsync(path + ".pending", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        File.Move(path + ".pending", path);
    }
}
