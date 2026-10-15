using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using Wf.Contracts;
using Wf.Sdk;
using Wf.Sdk.Hosting;

var workflow = Workflow.Define("tests/hosts/fixture", "1.0.0", w => w.Type("test")
    .Variables(v => v.Int("amount", 10))
    .Node("source", n => n.Task("noop", ctx => ctx.Outputs["value"] = 7))
    .Node("work", n => n.DependsOn("source").Input("source", "value")
        .Task("echo", ctx =>
        {
            ctx.Outputs["result"] = ctx.Input<int>("source", "value") + ctx.Var<int>("amount") + ctx.Outputs.Get<int>("previous");
            ctx.Outputs["pid"] = Environment.ProcessId;
            ctx.Outputs["taskKey"] = ctx.TaskKey;
            ctx.Log("Executed actual SDK task delegate.");
        })
        .Task("fail", _ => throw new InvalidOperationException("fixture failure"))
        .Task("nonretryable", _ => throw new NonRetryableException("do not retry"))
        .Task("selfcancel", _ => throw new OperationCanceledException("self cancellation"))
        .Task("permissions", ctx =>
        {
            var directory = Path.GetDirectoryName(args[2])!;
            if (OperatingSystem.IsWindows())
            {
                using var identity = WindowsIdentity.GetCurrent();
                var rules = new DirectoryInfo(directory).GetAccessControl()
                    .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
                ctx.Outputs["private"] = rules.Count == 1 && rules[0] is FileSystemAccessRule rule &&
                    rule.IdentityReference.Equals(identity.User) && rule.AccessControlType == AccessControlType.Allow &&
                    rule.FileSystemRights == FileSystemRights.FullControl && !rule.IsInherited;
            }
            else ctx.Outputs["private"] = File.GetUnixFileMode(directory) ==
                (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        })
        .Task("delay", async ctx => await Task.Delay(TimeSpan.FromSeconds(30), ctx.CancellationToken))
        .Task("badcallback", async ctx =>
        {
            using var registration = ctx.CancellationToken.Register(() => throw new InvalidOperationException("fixture bad cancellation callback"));
            await Task.Delay(TimeSpan.FromSeconds(30), ctx.CancellationToken);
        })
        .Task("block", ctx =>
        {
            ctx.Outputs["pid"] = Environment.ProcessId;
            Thread.Sleep(Timeout.Infinite);
        })
        .Task("child", ctx =>
        {
            var marker = ctx.Input<string>("source", "value")!;
            var info = OperatingSystem.IsWindows() ? new ProcessStartInfo("cmd.exe") : new ProcessStartInfo("/bin/sh");
            info.UseShellExecute = false;
            if (OperatingSystem.IsWindows())
            {
                info.ArgumentList.Add("/d"); info.ArgumentList.Add("/c"); info.ArgumentList.Add("ping -n 120 127.0.0.1 >nul");
            }
            else { info.ArgumentList.Add("-c"); info.ArgumentList.Add("sleep 120"); }
            using var child = Process.Start(info)!;
            File.WriteAllText(marker, child.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Thread.Sleep(Timeout.Infinite);
        })));

if (args.Length == 3 && args[0] == "--task-host" && args[1] == "--socket")
    return await TaskHostServer.RunAsync(workflow, args[2]);
return 2;

namespace Wf.TaskHost.Fixture
{
    public sealed class FixtureMarker;
}
