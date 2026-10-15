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

    public static readonly ValidationRule DependencyCycle = new("WFV005",
        "The workflow contains a dependency cycle.",
        "Remove circular node dependencies so the workflow is a directed acyclic graph.");

    public static readonly ValidationRule UnknownDependency = new("WFV006",
        "A dependency refers to an unknown node.",
        "Reference an existing node key or remove the dependency.");

    public static readonly ValidationRule DuplicateKey = new("WFV007",
        "A key is duplicated within its scope.",
        "Use unique node and variable keys, task keys within each node, and dependency references.");

    public static readonly ValidationRule VariableType = new("WFV008",
        "A variable declaration or default does not match its type or allowed values.",
        "Use a supported variable type, compatible allowed values, and a default matching those constraints.");

    public static readonly ValidationRule VariableRequired = new("WFV009",
        "A variable required constraint is not a boolean.",
        "Set required to true or false; required variables without defaults must be supplied when starting a run.");

    public static readonly ValidationRule VersionBump = new("WFV010",
        "The workflow version does not satisfy the required API compatibility bump.",
        "Increase the version by the required MAJOR, MINOR or PATCH level.");

    public static ImmutableArray<ValidationRule> All { get; } =
        [DirectoryNotFound, WorkflowNotFound, BuildFailed, DescribeFailed, DependencyCycle, UnknownDependency,
            DuplicateKey, VariableType, VariableRequired, VersionBump];
}
