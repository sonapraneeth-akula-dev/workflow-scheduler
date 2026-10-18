using System.Runtime.CompilerServices;
using Wf.Contracts.Definition;

namespace Wf.Sdk;

public static partial class Tasks
{
    private static readonly ConditionalWeakTable<Delegate, TaskAction> Declarations = new();

    internal static TaskAction Describe(Delegate handler) =>
        Declarations.TryGetValue(handler, out var action) ? action :
            new(TaskActionKind.SdkFunction, $"{handler.Method.DeclaringType?.FullName ?? "<global>"}.{handler.Method.Name}");

    private static Func<TaskContext, Task> Declare(Func<TaskContext, Task> handler, TaskAction action)
    {
        Declarations.Add(handler, action);
        return handler;
    }
}
