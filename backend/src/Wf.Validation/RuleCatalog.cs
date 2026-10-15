using System.Collections.Immutable;

namespace Wf.Validation;

public sealed record ValidationRule(string Id, string Message, string Hint)
{
    public ValidationError CreateError(string file, string path) => new(Id, file, path, Message, Hint);
}

/// <summary>
/// Stable identifiers shared by offline tests and registration. Add manifest, schedule and registry rules
/// here as they are implemented; do not renumber or reuse an existing identifier.
/// </summary>
public static class RuleCatalog
{
    public static readonly ValidationRule DirectoryNotFound = new("WFV001",
        "The workflow directory does not exist or is not a directory.",
        "Pass an existing workflow directory.");

    public static readonly ValidationRule WorkflowNotFound = new("WFV002",
        "The workflow directory does not contain workflow.cs.",
        "Create workflow.cs in the workflow directory.");

    public static readonly ValidationRule BuildFailed = new("WFV003",
        "The workflow script could not be built.",
        "Fix the compiler or restore diagnostics and validate again.");

    public static readonly ValidationRule DescribeFailed = new("WFV004",
        "The workflow script failed during --describe.",
        "Fix the workflow definition or --describe diagnostics and validate again.");

    public static ImmutableArray<ValidationRule> All { get; } =
        [DirectoryNotFound, WorkflowNotFound, BuildFailed, DescribeFailed];
}
