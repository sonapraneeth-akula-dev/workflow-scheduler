using Wf.Validation.Build;
using Wf.Validation.Rules;
using Wf.Validation.Schedules;

namespace Wf.Validation;

/// <summary>
/// Publishes a trusted workflow.cs and reads its --describe manifest without executing tasks.
/// Validates manifest, optional schedule-file and optional team-registry semantics. This is not a sandbox.
/// SemVer comparisons are a separate step.
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
    /// Manifest semantic errors return structured errors before contract construction.
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
            cancellationToken.ThrowIfCancellationRequested();
            var errors = ManifestRules.Validate(build.Manifest, cancellationToken: cancellationToken);
            var schedules = Path.Combine(root, ScheduleFileValidator.FileName);
            if (HasAttributes(schedules, FileAttributes.Directory, expected: true))
                errors = errors.Add(RuleCatalog.ScheduleShape.CreateError(ScheduleFileValidator.FileName, "$"));
            else if (HasAttributes(schedules, FileAttributes.Directory, expected: false))
            {
                var contents = await File.ReadAllBytesAsync(schedules, cancellationToken).ConfigureAwait(false);
                errors = errors.AddRange(ScheduleFileValidator.Validate(contents, build.Manifest.Variables,
                    cancellationToken: cancellationToken).Errors);
            }
            var team = FindTeamRegistry(root);
            if (team is not null)
            {
                var file = Path.GetRelativePath(root, team).Replace(Path.DirectorySeparatorChar, '/');
                if (HasAttributes(team, FileAttributes.Directory, expected: true))
                    errors = errors.Add(TeamRegistryRules.RegistryShape.CreateError(file, "$"));
                else
                {
                    var contents = await File.ReadAllBytesAsync(team, cancellationToken).ConfigureAwait(false);
                    errors = errors.AddRange(TeamRegistryRules.ValidateWorkflow(contents, build.Manifest.Metadata,
                        file: file, cancellationToken: cancellationToken));
                }
            }
            return new(errors, build);
        }
        catch (ManifestValidationException error)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(error.Errors);
        }
        catch (ScriptBuildException error) when (error.Stage is "publish" or "describe")
        {
            var rule = error.Stage == "publish" ? RuleCatalog.BuildFailed : RuleCatalog.DescribeFailed;
            return new([rule.CreateError("workflow.cs", "$")], buildFailure: error);
        }
    }

    private static string? FindTeamRegistry(string root)
    {
        // teams/<team>/<type>/<workflow> keeps team.json two levels above the workflow.
        string? directory = root;
        for (var level = 0; directory is not null && level <= 2;
             directory = Path.GetDirectoryName(directory), level++)
        {
            var candidate = Path.Combine(directory, "team.json");
            if (HasAttributes(candidate, FileAttributes.Directory, expected: true) ||
                HasAttributes(candidate, FileAttributes.Directory, expected: false))
                return candidate;
        }
        return null;
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
