using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Wf.Sdk;

namespace Wf.TaskHost.Fixture;

public static class ResourceLimitFixture
{
    public static WorkflowDefinition Create() => Workflow.Define("tests/resource-limits", "1.0.0", w => w.Type("test")
        .Variables(v => v.String("marker").String("runtimeConfig").String("depsFile"))
        .Node("work", n => n.Task("memory", ctx => Memory(ctx, caught: false))
            .Task("caughtMemory", ctx => Memory(ctx, caught: true))
            .Task("childMemory", ctx => Child(ctx, "memory"))
            .Task("cpu", ctx => Cpu(ctx, child: false))
            .Task("childCpu", ctx => Cpu(ctx, child: true))
            .Task("echo", ctx => ctx.Outputs["pid"] = Environment.ProcessId)));

    public static int RunChild(string[] arguments)
    {
        File.WriteAllText(arguments[1], Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        if (arguments[0] == "memory") Allocate();
        else File.WriteAllText(arguments[1] + ".done", Burn().ToString(CultureInfo.InvariantCulture));
        return 0;
    }

    private static void Ready(TaskContext context)
    {
        var marker = context.Var<string>("marker");
        File.WriteAllText(marker + ".next", JsonSerializer.Serialize(new { pid = Environment.ProcessId,
            supervisor = int.TryParse(Environment.GetEnvironmentVariable("WF_TASK_HOST_SUPERVISOR_PID"), out var pid)
                ? pid : Environment.ProcessId,
            cgroup = OperatingSystem.IsLinux() ? File.ReadAllText("/proc/self/cgroup") : null }));
        File.Move(marker + ".next", marker);
    }

    private static void Memory(TaskContext context, bool caught)
    {
        Ready(context);
        try { Allocate(); }
        catch (OutOfMemoryException) when (caught) { context.Outputs["caught"] = true; }
    }

    private static void Allocate()
    {
        var allocations = new List<nint>();
        try
        {
            for (var block = 0; block < 256; block++)
            {
                var allocation = Marshal.AllocHGlobal(4 * 1024 * 1024);
                allocations.Add(allocation);
                for (var page = 0; page < 4 * 1024 * 1024; page += 4096)
                    Marshal.WriteByte(allocation, page, 1);
            }
            throw new InvalidOperationException("RAM cap did not reject a 1 GiB workload.");
        }
        finally { foreach (var allocation in allocations) Marshal.FreeHGlobal(allocation); }
    }

    public static async Task ExecutePackagedMemory(TaskContext context)
    {
        var marker = context.Var<string>("checkpointMarker");
        File.WriteAllText(marker + ".next", JsonSerializer.Serialize(new { pid = Environment.ProcessId,
            supervisor = int.TryParse(Environment.GetEnvironmentVariable("WF_TASK_HOST_SUPERVISOR_PID"), out var pid)
                ? pid : Environment.ProcessId }));
        File.Move(marker + ".next", marker);
        while (!File.Exists(marker + ".go")) await Task.Delay(10, context.CancellationToken);
        if (context.Var<int>("amount") == 1)
        {
            var info = new ProcessStartInfo("dotnet") { UseShellExecute = false };
            foreach (var argument in new[] { typeof(ResourceLimitFixture).Assembly.Location,
                         "--resource-limit-child", "memory", marker + ".child" })
                info.ArgumentList.Add(argument);
            using var child = Process.Start(info) ?? throw new InvalidOperationException("Packaged RAM child did not start.");
            await child.WaitForExitAsync(context.CancellationToken);
        }
        else
        {
            try { Allocate(); }
            catch (OutOfMemoryException) when (context.Var<int>("amount") == 2)
            { context.Outputs["caught"] = true; }
        }
    }

    private static double Burn()
    {
        using var process = Process.GetCurrentProcess();
        var before = process.TotalProcessorTime;
        var end = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 3;
        Parallel.For(0, 4, new ParallelOptions { MaxDegreeOfParallelism = 4 }, _ =>
        {
            while (Stopwatch.GetTimestamp() < end) Thread.SpinWait(1000);
        });
        process.Refresh();
        return (process.TotalProcessorTime - before).TotalSeconds;
    }

    private static Process StartChild(TaskContext context, string mode)
    {
        var info = new ProcessStartInfo("dotnet") { UseShellExecute = false };
        foreach (var argument in new[] { "exec", "--runtimeconfig", context.Var<string>("runtimeConfig"),
                     "--depsfile", context.Var<string>("depsFile"), typeof(ResourceLimitFixture).Assembly.Location,
                     "--resource-limit-child", mode, context.Var<string>("marker") + ".child" })
            info.ArgumentList.Add(argument);
        return Process.Start(info) ?? throw new InvalidOperationException("Resource workload child did not start.");
    }

    private static void Child(TaskContext context, string mode)
    {
        Ready(context);
        using var child = StartChild(context, mode);
        child.WaitForExit();
        context.Outputs["childExit"] = child.ExitCode;
    }

    private static void Cpu(TaskContext context, bool child)
    {
        Ready(context);
        using var process = child ? StartChild(context, "cpu") : null;
        var watch = Stopwatch.StartNew();
        var cpu = Burn();
        if (process is not null)
        {
            process.WaitForExit();
            if (process.ExitCode != 0) throw new InvalidOperationException("CPU child failed.");
            cpu += double.Parse(File.ReadAllText(context.Var<string>("marker") + ".child.done"), CultureInfo.InvariantCulture);
        }
        context.Outputs["cpuSeconds"] = cpu;
        context.Outputs["elapsedSeconds"] = watch.Elapsed.TotalSeconds;
    }
}
