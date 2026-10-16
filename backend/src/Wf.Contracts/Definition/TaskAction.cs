namespace Wf.Contracts.Definition;

public enum TaskActionKind { SdkFunction, Command, Http, HttpOperation }

public sealed record TaskAction
{
    public TaskActionKind Kind { get; }
    public string Declaration { get; }

    public TaskAction(TaskActionKind kind, string declaration)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        ArgumentException.ThrowIfNullOrWhiteSpace(declaration);
        if (declaration.Length > 4096 || declaration.Any(char.IsControl))
            throw new ArgumentException("Task declarations must fit 4096 characters without control characters.", nameof(declaration));
        Kind = kind;
        Declaration = declaration;
    }
}
