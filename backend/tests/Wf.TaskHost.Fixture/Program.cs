using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.Sdk;

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
        .Task("logs", async ctx =>
        {
            ctx.Outputs["pid"] = Environment.ProcessId;
            var secret = ctx.Input<string>("source", "value")!;
            Console.Out.Write("stdout sec");
            Console.Out.Flush();
            await Task.Delay(30, ctx.CancellationToken);
            Console.Out.WriteLine("ret-token tail");
            Console.Error.WriteLine("stderr " + secret);
            ctx.Log("ctx " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(secret)));
            Console.Out.Write("unterminated");
        })
        .Task("unicodeLogs", ctx =>
        {
            ctx.Outputs["pid"] = Environment.ProcessId;
            Console.Out.Write(new string('x', 16383) + "\U0001F511\r\n");
            Console.Out.Write(new string('y', 16384) + "\n\n");
        })
        .Task("nonretryable", _ => throw new NonRetryableException("do not retry"))
        .Task("selfcancel", _ => throw new OperationCanceledException("self cancellation"))
        .Task("classified", ctx => throw new TaskFailureException("Scripted explicit signal; not external fault injection.",
            category: Enum.Parse<FailureCategory>(ctx.Input<string>("source", "value")!)))
        .Task("cancelerror", async ctx =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ctx.CancellationToken); }
            catch (OperationCanceledException) { throw new InvalidOperationException("ordinary error after cancellation"); }
        })
        .Task("cancelauthor", async ctx =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ctx.CancellationToken); }
            catch (OperationCanceledException) { throw new NonRetryableException("author error after cancellation"); }
        })
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
        .Task("agentloss", async ctx =>
        {
            var delay = Environment.GetEnvironmentVariable("WF_FIXTURE_AGENT_LOSS_HOLD") == "true"
                ? TimeSpan.FromSeconds(30) : TimeSpan.Zero;
            await Task.Delay(delay, ctx.CancellationToken);
            ctx.Outputs["pid"] = Environment.ProcessId;
        })
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
        }))
    .PostProcess(s => s.Task("report", async ctx =>
    {
        ctx.Outputs["pid"] = Environment.ProcessId;
        ctx.Outputs["sourceKey"] = ctx.TaskKey;
        await ctx.Reports.PublishAsync("text", "Post summary",
            System.Text.Encoding.UTF8.GetBytes("Published by " + ctx.TaskKey), ctx.CancellationToken, ["post", "review"]);
    }).Task("artifact", async ctx =>
    {
        await ctx.Reports.PublishAsync("text", "Artifact summary",
            System.Text.Encoding.UTF8.GetBytes("Artifact publication from real task host."), ctx.CancellationToken);
        using var input = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("Published by " + ctx.TaskKey));
        var uri = await ctx.Artifacts.PublishAsync("response.bin", input, "application/octet-stream", ctx.CancellationToken);
        ctx.Outputs["artifact"] = uri.AbsoluteUri;
    }).Task("reportBurst", async ctx =>
    {
        for (var index = 0; index < 12; index++)
            await ctx.Reports.PublishAsync("custom", "Report " + index,
                System.Text.Encoding.UTF8.GetBytes(index.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ctx.CancellationToken, ["burst"]);
    })));

return await workflow.RunAsync(args);

namespace Wf.TaskHost.Fixture
{
    public sealed class FixtureMarker;
}
