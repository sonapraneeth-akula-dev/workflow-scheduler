using Wf.Contracts.Definition;

namespace Wf.Engine.Core.Placement;

public static class PlacementFilter
{
    public static void EnsureResolved(PlacementSelector? selector)
    {
        if (selector is not null &&
            (selector.Os?.Variable is not null || selector.Pool?.Variable is not null ||
             selector.Labels.Any(label => label.Value?.Variable is not null) || selector.Machines.Any(machine => machine.Variable is not null)))
            throw new ArgumentException("Placement selectors must be resolved before allocation.", nameof(selector));
    }

    public static bool Matches(PlacementSelector? selector, PlacementAgent agent)
    {
        if (selector is null) return true;
        if (selector.Os?.Literal is { } os && os != agent.Os || selector.Pool?.Literal is { } pool && pool != agent.Pool)
            return false;
        if (!selector.MachineIds.IsEmpty && !selector.MachineIds.Contains(agent.Id)) return false;
        if (!selector.Machines.IsEmpty && !selector.Machines.Any(machine => machine.Literal == agent.Name)) return false;
        foreach (var label in selector.Labels)
            if (agent.Labels is null || !agent.Labels.TryGetValue(label.Key, out var value) ||
                label.Value?.Literal is { } expected && expected != value) return false;
        return true;
    }
}
