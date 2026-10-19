using System.Collections.Immutable;
using Wf.Validation.Rules;

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

    public static readonly ValidationRule ScheduleShape = new("WFV011",
        "The schedule file does not match its JSON contract.",
        "Use schedules.schema.json; provide instances, nonempty IDs and timezones, and only supported properties.");

    public static readonly ValidationRule ScheduleExpression = new("WFV012",
        "A schedule expression or timezone is invalid.",
        "Provide exactly one valid five-field cron or positive whole-second constant-format interval (at most 2147483647 seconds) and a recognized IANA timezone.");

    public static readonly ValidationRule ScheduleVariable = new("WFV013",
        "A schedule variable is unknown, invalid, duplicated across categories, or required but missing.",
        "Use declared variable keys and valid values; declare each default once and supply required instance values.");

    public static readonly ValidationRule ScheduleIdentity = new("WFV014",
        "A schedule instance ID is empty, duplicated, or contains control characters.",
        "Give every instance a nonempty, stable, unique ID without control characters.");

    public static readonly ValidationRule TriggerVariable = new("WFV025",
        "A run variable is unknown, invalid, or required but missing.",
        "Use a declared variable key and its JSON type and allowed values; supply required values without defaults. Values cannot contain NUL characters.");

    public static readonly ValidationRule ScheduleVersionPin = new("WFV026",
        "The schedule version pin is invalid.",
        "Use an exact version UUID or SemVer, comparison ranges such as >=1.0.0 <2.0.0, || alternatives, or *, major.* or major.minor.* wildcards, within 256 characters.");

    public static readonly ValidationRule MachineSelector = new("WFV027",
        "A machine selector is invalid or cannot be satisfied by registered machines.",
        "Use known machine IDs and satisfiable OS, pool, name, label and count constraints.");

    public static readonly ValidationRule InvalidCondition = new("WFV028",
        "An execution condition or its reference is invalid.",
        "Use one declared Boolean variable or output input. Only later series tasks may read their own node's outputs.");

    public static ImmutableArray<ValidationRule> All { get; } =
        [DirectoryNotFound, WorkflowNotFound, BuildFailed, DescribeFailed, DependencyCycle, UnknownDependency,
            DuplicateKey, VariableType, VariableRequired, VersionBump, ScheduleShape, ScheduleExpression,
            ScheduleVariable, ScheduleIdentity, TeamRegistryRules.RegistryShape, TeamRegistryRules.DuplicateType,
            TeamRegistryRules.TypeQuota, TeamRegistryRules.InvalidMetadataSchema, TeamRegistryRules.UnknownType,
            TeamRegistryRules.InvalidMetadata, HttpOperationRules.InvalidContract, HttpOperationRules.UnsafeRetry,
            HttpOperationRules.InsufficientRetention, PresentationValidator.InvalidPresentation, TriggerVariable, ScheduleVersionPin,
            MachineSelector, InvalidCondition];
}
