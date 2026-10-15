using System.Diagnostics;
using System.Text.Json;
using Wf.Contracts;
using Wf.Sdk.Hosting;
using Wf.Validation.Build;

namespace Wf.Sdk.Testing;

/// <summary>
/// Publishes a trusted .NET file-app once per content hash and executes its production DLL out of process.
/// File-based directives cannot be included in a regular test project's compilation (CS9298).
/// This executes trusted workflow code, not a sandbox.
/// </summary>
public sealed class WorkflowUnderTest
{
    private readonly string workingDirectory;

    private WorkflowUnderTest(ScriptBuildResult build, string workingDirectory)
    {
        BuildResult = build;
        this.workingDirectory = workingDirectory;
    }

    public ScriptBuildResult BuildResult { get; }

    /// <summary>Synchronous fixture entry point; asynchronous callers should use BuildAsync.</summary>
    public static WorkflowUnderTest Build(string scriptPath, ScriptBuildCache? buildCache = null,
        string? sourceRoot = null, IReadOnlyList<string>? additionalInputs = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => BuildAsync(scriptPath, buildCache, sourceRoot, additionalInputs, cancellationToken), cancellationToken)
            .GetAwaiter().GetResult();

    /// <summary>
    /// Uses ScriptBuildCache without a second caching layer. Defaults to bin/wf-sdk-testing-cache and
    /// obj/wf-sdk-testing-artifacts under sourceRoot (the script directory when omitted).
    /// Include the complete local dependency/import closure in additionalInputs.
    /// Build, describe, cache-integrity and filesystem errors propagate; stale artifacts are never substituted.
    /// </summary>
    public static async Task<WorkflowUnderTest> BuildAsync(string scriptPath, ScriptBuildCache? buildCache = null,
        string? sourceRoot = null, IReadOnlyList<string>? additionalInputs = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptPath);
        var script = Path.GetFullPath(scriptPath);
        var directory = Path.GetDirectoryName(script)!;
        if (sourceRoot is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        var root = sourceRoot is null ? directory : Path.GetFullPath(sourceRoot);
        buildCache ??= new ScriptBuildCache(Path.Combine(root, "bin", "wf-sdk-testing-cache"),
            Path.Combine(root, "obj", "wf-sdk-testing-artifacts"));
        var build = await buildCache.BuildAsync(new(script, root, additionalInputs), cancellationToken).ConfigureAwait(false);
        return new(build, directory);
    }

    public DryRunLog Run(MockSpec mocks, CancellationToken cancellationToken = default) =>
        Task.Run(() => RunAsync(mocks, cancellationToken), cancellationToken).GetAwaiter().GetResult();

    /// <summary>
    /// Runs dotnet &lt;published assembly&gt; --dry-run using a unique, cleaned-up spec under the script's obj directory.
    /// Failed/timed-out workflow logs (exit 1) are returned normally. Host/protocol failures throw
    /// WorkflowRunException with stdout/stderr. Cancellation kills the process tree before returning.
    /// Each invocation starts fresh attempt counters; concurrent runs do not share mutable state.
    /// </summary>
    public async Task<DryRunLog> RunAsync(MockSpec mocks, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(mocks);
        var runDirectory = Path.Combine(workingDirectory, "obj", "wf-sdk-testing-runs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runDirectory);
        try
        {
            var path = Path.Combine(runDirectory, "mocks.json");
            await File.WriteAllTextAsync(path, mocks.ToJson(), cancellationToken).ConfigureAwait(false);
            var (exitCode, output, error) = await ExecuteAsync(path, cancellationToken).ConfigureAwait(false);
            try
            {
                var log = JsonSerializer.Deserialize(output, DryRunJsonContext.Default.DryRunLog);
                if (log?.Snapshot is null || log.Events.IsDefault || log.Attempts.IsDefault || log.Outputs is null ||
                    log.Snapshot.RunId == Guid.Empty || log.Snapshot.Nodes is null || log.Snapshot.Tasks is null ||
                    log.Snapshot.Metadata is null || log.Snapshot.Variables is null || log.Snapshot.EndedAt is null ||
                    log.Snapshot.Status is not (RunStatus.Succeeded or RunStatus.Failed or RunStatus.Aborted or RunStatus.TimedOut))
                    throw new JsonException("Dry-run output must contain a completed run, events, attempts and outputs.");
                if (exitCode != log.ExitCode)
                    throw new JsonException("Dry-run exit code does not match the completed run status.");
                return log;
            }
            catch (JsonException exception)
            {
                throw new WorkflowRunException(exitCode, output, error, exception);
            }
        }
        finally
        {
            Directory.Delete(runDirectory, recursive: true);
        }
    }

    private async Task<(int ExitCode, string Output, string Error)> ExecuteAsync(string specPath,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(BuildResult.AssemblyPath);
        start.ArgumentList.Add("--dry-run");
        start.ArgumentList.Add(specPath);
        using var process = new Process { StartInfo = start };
        cancellationToken.ThrowIfCancellationRequested();
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(output, error).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) when (process.HasExited) { }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            try { await Task.WhenAll(output, error).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            throw;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return (process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
    }
}

/// <summary>A host failure or invalid dry-run response, not an expected failed workflow outcome.</summary>
public sealed class WorkflowRunException(int exitCode, string standardOutput, string standardError,
    Exception? innerException = null)
    : Exception($"Workflow dry-run failed or returned an invalid event log (exit code {exitCode})." +
        $"{Environment.NewLine}{standardError}{Environment.NewLine}{standardOutput}", innerException)
{
    public int ExitCode { get; } = exitCode;
    public string StandardOutput { get; } = standardOutput;
    public string StandardError { get; } = standardError;
}
