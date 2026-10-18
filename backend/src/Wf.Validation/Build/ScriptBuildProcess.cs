using System.Diagnostics;
using System.Text;

namespace Wf.Validation.Build;

internal static class ScriptBuildProcess
{
    internal static async Task<byte[]> RunAsync(string dotnet, IEnumerable<string> arguments,
        string workingDirectory, string stage, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(dotnet)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        process.Start();
        using var output = new MemoryStream();
        using var error = new MemoryStream();
        var readOutput = process.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken);
        var readError = process.StandardError.BaseStream.CopyToAsync(error, cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(readOutput, readError);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) when (process.HasExited)
            {
                // The child won the race with cancellation.
            }
            await process.WaitForExitAsync(CancellationToken.None);
            try
            {
                await Task.WhenAll(readOutput, readError);
            }
            catch (OperationCanceledException)
            {
                // Cancellation also interrupts redirected-pipe reads.
            }
            throw;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
            throw new ScriptBuildException(stage, process.ExitCode,
                Encoding.UTF8.GetString(output.ToArray()), Encoding.UTF8.GetString(error.ToArray()));
        return output.ToArray();
    }
}
