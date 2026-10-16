using System.Collections.Immutable;
using System.Diagnostics;
using Wf.Contracts;
using Wf.Contracts.Definition;

namespace Wf.Sdk;

public sealed class CommandTaskException(int exitCode, bool retryable)
    : TaskFailureException($"Command task exited with code {exitCode}.", retryable)
{
    public int ExitCode { get; } = exitCode;
}

public static partial class Tasks
{
    public static Func<TaskContext, Task> Command(string exe, string args = "", string? workingDirectory = null,
        IReadOnlyDictionary<string, string?>? environment = null, IEnumerable<int>? retryableExitCodes = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        return Command(exe, args, null, workingDirectory, environment, retryableExitCodes);
    }

    public static Func<TaskContext, Task> Command(string exe, IEnumerable<string> args, string? workingDirectory = null,
        IReadOnlyDictionary<string, string?>? environment = null, IEnumerable<int>? retryableExitCodes = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        var arguments = args.ToImmutableArray();
        if (arguments.Any(a => a is null))
            throw new ArgumentException("Command arguments cannot contain null.", nameof(args));
        return Command(exe, null, arguments, workingDirectory, environment, retryableExitCodes);
    }

    private static Func<TaskContext, Task> Command(string exe, string? rawArgs, ImmutableArray<string>? args,
        string? workingDirectory, IReadOnlyDictionary<string, string?>? environment, IEnumerable<int>? retryableExitCodes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exe);
        var copiedEnvironment = environment?.ToImmutableDictionary(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal) ??
            ImmutableDictionary<string, string?>.Empty;
        foreach (var (key, value) in copiedEnvironment)
            if (string.IsNullOrWhiteSpace(key) || key.IndexOfAny(['=', '\0']) >= 0 || value?.Contains('\0') == true)
                throw new ArgumentException("Command environment contains an invalid name or value.", nameof(environment));
        var retryable = retryableExitCodes?.ToImmutableHashSet();
        if (retryable?.Contains(0) == true)
            throw new ArgumentException("A successful exit code cannot be retryable.", nameof(retryableExitCodes));
        return Declare(async context =>
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var info = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = workingDirectory ?? "" };
            if (args is { } arguments)
                foreach (var argument in arguments)
                    info.ArgumentList.Add(argument);
            else
                info.Arguments = rawArgs!;
            foreach (var (key, value) in copiedEnvironment)
                info.Environment[key] = value;
            using var process = new Process { StartInfo = info };
            if (!process.Start())
                throw new InvalidOperationException("Command task did not start.");
            try
            {
                await process.WaitForExitAsync(context.CancellationToken);
            }
            catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
                throw;
            }
            if (process.ExitCode != 0)
                throw new CommandTaskException(process.ExitCode, retryable?.Contains(process.ExitCode) ?? true);
        }, new(TaskActionKind.Command, exe));
    }
}
