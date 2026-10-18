using Wf.Contracts.Manifest;
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
    /// <summary>
    /// Validates an already-described manifest and optional companion files without compiling or running source.
    /// Registration must validate raw manifest semantics before constructing the typed contract.
    /// </summary>
    public static ValidationResult Validate(WorkflowManifest manifest, ReadOnlyMemory<byte>? schedules = null,
        ReadOnlyMemory<byte>? registry = null, string scheduleFile = ScheduleFileValidator.FileName,
        string registryFile = TeamRegistryRules.FileName, ValidationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(manifest);
        var typeQuota = options?.TypeQuota ?? TeamRegistryRules.DefaultTypeQuota;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(typeQuota);
        var errors = ManifestRules.Validate(manifest, cancellationToken: cancellationToken);
        if (!errors.IsEmpty) return new(errors);
        if (schedules is { } scheduleContents)
            errors = errors.AddRange(ScheduleFileValidator.Validate(scheduleContents, manifest.Variables,
                scheduleFile, cancellationToken).Errors);
        if (registry is { } registryContents)
            errors = errors.AddRange(TeamRegistryRules.ValidateWorkflow(registryContents, manifest.Metadata,
                registryFile, typeQuota: typeQuota, cancellationToken: cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();
        return new(errors);
    }

    /// <summary>Synchronous test/CLI entry point; use ValidateAsync in asynchronous callers.</summary>
    public static ValidationResult Validate(string directory, ScriptBuildCache? buildCache = null,
        IReadOnlyList<string>? additionalInputs = null, CancellationToken cancellationToken = default,
        ValidationOptions? options = null) =>
        Task.Run(() => ValidateAsync(directory, buildCache, additionalInputs, cancellationToken, options), cancellationToken)
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
        IReadOnlyList<string>? additionalInputs = null, CancellationToken cancellationToken = default,
        ValidationOptions? options = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var typeQuota = options?.TypeQuota ?? TeamRegistryRules.DefaultTypeQuota;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(typeQuota);
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
            var schedules = Path.Combine(root, ScheduleFileValidator.FileName);
            ReadOnlyMemory<byte>? scheduleContents = null;
            if (HasAttributes(schedules, FileAttributes.Directory, expected: false))
                scheduleContents = await File.ReadAllBytesAsync(schedules, cancellationToken).ConfigureAwait(false);
            var errors = Validate(build.Manifest, scheduleContents, options: options,
                cancellationToken: cancellationToken).Errors;
            if (HasAttributes(schedules, FileAttributes.Directory, expected: true))
                errors = errors.Add(RuleCatalog.ScheduleShape.CreateError(ScheduleFileValidator.FileName, "$"));
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
                        file: file, typeQuota: typeQuota, cancellationToken: cancellationToken));
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
