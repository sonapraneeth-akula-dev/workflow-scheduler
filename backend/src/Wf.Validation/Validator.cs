using Wf.Validation.Build;

namespace Wf.Validation;

/// <summary>
/// Publishes a trusted workflow.cs and reads its --describe manifest without executing tasks.
/// This is not a sandbox. Manifest, schedule, registry and SemVer rules are separate implementation steps.
/// </summary>
public static class Validator
{
    /// <summary>Synchronous test/CLI entry point; use ValidateAsync in asynchronous callers.</summary>
    public static ValidationResult Validate(string directory, ScriptBuildCache? buildCache = null,
        IReadOnlyList<string>? additionalInputs = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => ValidateAsync(directory, buildCache, additionalInputs, cancellationToken), cancellationToken)
            .GetAwaiter().GetResult();

    /// <summary>
    /// Expected missing inputs and publish/describe nonzero exits return errors. Cancellation, process-start,
    /// SDK, filesystem, cache-integrity and malformed manifest-contract failures propagate as exceptions.
    /// Defaults cache published output under bin and isolated build intermediates under obj in directory.
    /// Supply additionalInputs for local references/imports outside directory; include their complete dependency closure.
    /// Cache behavior and declared-input limitations follow ScriptBuildCache.
    /// </summary>
    public static async Task<ValidationResult> ValidateAsync(string directory, ScriptBuildCache? buildCache = null,
        IReadOnlyList<string>? additionalInputs = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var root = Path.GetFullPath(directory);
        if (!HasAttributes(root, FileAttributes.Directory, expected: true))
            return new([RuleCatalog.DirectoryNotFound.CreateError(".", "$")]);
        var script = Path.Combine(root, "workflow.cs");
        if (!HasAttributes(script, FileAttributes.Directory, expected: false))
            return new([RuleCatalog.WorkflowNotFound.CreateError("workflow.cs", "$")]);

        buildCache ??= new ScriptBuildCache(Path.Combine(root, "bin", "wf-validation-cache"),
            Path.Combine(root, "obj", "wf-validation-artifacts"));
        try
        {
            var build = await buildCache.BuildAsync(new(script, root, additionalInputs), cancellationToken)
                .ConfigureAwait(false);
            return new([], build);
        }
        catch (ScriptBuildException error) when (error.Stage is "publish" or "describe")
        {
            var rule = error.Stage == "publish" ? RuleCatalog.BuildFailed : RuleCatalog.DescribeFailed;
            return new([rule.CreateError("workflow.cs", "$")], buildFailure: error);
        }
    }

    private static bool HasAttributes(string path, FileAttributes attributes, bool expected)
    {
        try
        {
            return ((File.GetAttributes(path) & attributes) != 0) == expected;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }
}
