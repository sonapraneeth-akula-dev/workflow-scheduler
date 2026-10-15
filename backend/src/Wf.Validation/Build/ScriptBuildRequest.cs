using Wf.Contracts.Manifest;

namespace Wf.Validation.Build;

/// <summary>
/// SourceRoot is a closed local input tree containing ScriptPath. AdditionalInputs adds files or trees
/// outside it (including local project references and imported build files). bin/obj and tool-output
/// directories are excluded. Callers must include the complete dependency closure.
/// </summary>
public sealed record ScriptBuildRequest(string ScriptPath, string SourceRoot,
    IReadOnlyList<string>? AdditionalInputs = null);

public sealed record ScriptBuildResult(string CacheKey, string PublishDirectory, string AssemblyPath,
    ReadOnlyMemory<byte> ManifestUtf8, WorkflowManifest Manifest, bool CacheHit,
    TimeSpan KeyDuration, TimeSpan PublishDuration, TimeSpan DescribeDuration);

/// <summary>Compiler/describe diagnostics are preserved; callers must not silently retry with stale output.</summary>
public sealed class ScriptBuildException(string stage, int exitCode, string standardOutput, string standardError)
    : Exception($"{stage} failed with exit code {exitCode}.{Environment.NewLine}{standardError}{Environment.NewLine}{standardOutput}")
{
    public string Stage { get; } = stage;
    public int ExitCode { get; } = exitCode;
    public string StandardOutput { get; } = standardOutput;
    public string StandardError { get; } = standardError;
}
