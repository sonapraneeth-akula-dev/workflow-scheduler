using Wf.Contracts.Definition;
using Wf.Sdk;

namespace Wf.TaskHost.Fixture;

public static class SecretFixture
{
    public static WorkflowDefinition Create(bool withDefault) =>
        Workflow.Define("tests/hosts/secret-fixture", "1.0.0", workflow => workflow.Type("test")
            .Variables(variables => variables.Secret("credential", withDefault ? "secret://local/default-token" : null))
            .Node("work", node => node.Task("secret", ExecuteAsync)
                .Task("undeclared", context => context.Secret("not-declared"))));

    public static async Task ExecuteAsync(TaskContext context)
    {
        var reference = context.Var<SecretReference>("credential");
        var value = context.Secret(reference);
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value));
        context.Outputs["pid"] = Environment.ProcessId;
        context.Outputs["reference"] = reference.Name;
        context.Outputs["value"] = value;
        context.Outputs["encoded"] = encoded;
        context.Outputs["length"] = value.Length;
        context.Outputs["launchClean"] = !Environment.GetCommandLineArgs().Any(argument => argument.Contains(value, StringComparison.Ordinal)) &&
            !Environment.GetEnvironmentVariables().Values.Cast<string>().Any(variable => variable.Contains(value, StringComparison.Ordinal));
        var split = Math.Min(5, value.Length);
        Console.Out.Write("out " + value[..split]);
        Console.Out.Flush();
        Console.Error.Write(new string('x', 4094) + value[..split]);
        Console.Error.Flush();
        await Task.Delay(30, context.CancellationToken);
        Console.Out.WriteLine(value[split..] + " tail");
        Console.Error.WriteLine(value[split..] + " tail");
        context.Log("ctx " + value + " " + encoded);
        Console.Out.Write(value);
        Console.Out.Flush();
    }
}
