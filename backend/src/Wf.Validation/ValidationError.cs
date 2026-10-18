using System.Collections.Immutable;
using Wf.Validation.Build;

namespace Wf.Validation;

/// <summary>
/// Stable, machine-readable failure. File is relative to the workflow directory ('.' for the directory);
/// Path is a JSONPath, or '$' for a whole-file failure. Raw process diagnostics are kept separately.
/// </summary>
public sealed record ValidationError(string RuleId, string File, string Path, string Message, string Hint);

public sealed class ValidationResult
{
    public ImmutableArray<ValidationError> Errors { get; }
    public bool IsValid => Errors.IsEmpty;
    public ScriptBuildResult? Build { get; }

    /// <summary>
    /// Original publish/describe exit code, stdout and stderr. Unlike Errors, these diagnostics can contain
    /// machine-specific paths and timings; do not use them to compare local and server rule results.
    /// </summary>
    public ScriptBuildException? BuildFailure { get; }

    internal ValidationResult(ImmutableArray<ValidationError> errors, ScriptBuildResult? build = null,
        ScriptBuildException? buildFailure = null) =>
        (Errors, Build, BuildFailure) = (errors, build, buildFailure);
}
