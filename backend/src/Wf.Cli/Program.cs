using Wf.Cli.Commands;

namespace Wf.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Cache misses use random isolated intermediates. Portable PDB paths otherwise alter every assembly.
        // Set build defaults once in this dedicated CLI process; never discard files from publish output.
        Environment.SetEnvironmentVariable("Deterministic", "true");
        Environment.SetEnvironmentVariable("DebugType", "none");
        Environment.SetEnvironmentVariable("DebugSymbols", "false");
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, signal) =>
        {
            signal.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += handler;
        try
        {
            if (args is ["--help"] or ["-h"])
            {
                Console.WriteLine(ValidateCommand.Usage);
                Console.WriteLine(AbortCommand.Usage);
                Console.WriteLine(RunCommand.Usage);
                Console.WriteLine(PublishCommand.Usage);
                Console.WriteLine(CloneCommand.Usage);
                Console.WriteLine(LocalCommand.Usage);
                return 0;
            }
            if (args.Length > 0 && args[0] == "abort")
                return await AbortCommand.RunAsync(args[1..], Console.Out, Console.Error, cancellation.Token);
            if (args.Length > 0 && args[0] == "run")
                return await RunCommand.RunAsync(args[1..], Console.Out, Console.Error, cancellation.Token);
            if (args.Length > 0 && args[0] == "publish")
                return await PublishCommand.RunAsync(args[1..], Console.Out, Console.Error, cancellation.Token);
            if (args.Length > 0 && args[0] == "clone")
                return await CloneCommand.RunAsync(args[1..], Console.Out, Console.Error, cancellation.Token);
            if (args.Length > 0 && args[0] == "local")
                return await LocalCommand.RunAsync(args[1..], Console.Out, Console.Error, cancellation.Token);
            if (args.Length == 0 || args[0] != "validate")
            {
                Console.Error.WriteLine(ValidateCommand.Usage);
                Console.Error.WriteLine(AbortCommand.Usage);
                Console.Error.WriteLine(RunCommand.Usage);
                Console.Error.WriteLine(PublishCommand.Usage);
                Console.Error.WriteLine(CloneCommand.Usage);
                Console.Error.WriteLine(LocalCommand.Usage);
                return 2;
            }
            return await ValidateCommand.RunAsync(args[1..], Console.Out, Console.Error, cancellation.Token);
        }
        finally { Console.CancelKeyPress -= handler; }
    }
}
