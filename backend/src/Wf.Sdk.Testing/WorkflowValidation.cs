using Wf.Validation;
using Wf.Validation.Build;

namespace Wf.Sdk.Testing;

/// <summary>
/// Offline validation entry point for xUnit and other workflow tests. Compiles and describes the production
/// script through Wf.Validation; task execution and mocking belong to the separate dry-run harness.
/// </summary>
public static class WorkflowValidation
{
    public static ValidationResult Validate(string directory, ScriptBuildCache? buildCache = null,
        IReadOnlyList<string>? additionalInputs = null, CancellationToken cancellationToken = default,
        ValidationOptions? options = null) =>
        Validator.Validate(directory, buildCache, additionalInputs, cancellationToken, options);

    public static Task<ValidationResult> ValidateAsync(string directory, ScriptBuildCache? buildCache = null,
        IReadOnlyList<string>? additionalInputs = null, CancellationToken cancellationToken = default,
        ValidationOptions? options = null) =>
        Validator.ValidateAsync(directory, buildCache, additionalInputs, cancellationToken, options);
}
