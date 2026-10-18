using System.Collections.Immutable;

namespace Wf.Contracts.Definition;

public static class LifecycleStages
{
    public const string SystemInit = "$init";
    public const string PreSetup = "$pre";
    public const string PostProcess = "$post";
    public const string SystemPostProcess = "$sys-post";
    public static ImmutableArray<string> Before { get; } = [SystemInit, PreSetup];
    public static ImmutableArray<string> After { get; } = [PostProcess, SystemPostProcess];
    public static bool IsStage(string key) => Before.Contains(key) || After.Contains(key);
}
