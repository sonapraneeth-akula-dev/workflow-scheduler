using System.Collections.Immutable;
using Wf.Contracts.Definition;

namespace Wf.Sdk;

public static class DagValidator
{
    public static void Validate(ImmutableArray<NodeDefinition> nodes)
    {
        var byKey = new Dictionary<string, NodeDefinition>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (!byKey.TryAdd(node.Key, node))
                throw new ArgumentException($"Duplicate node key '{node.Key}'.");
            var tasks = new HashSet<string>(StringComparer.Ordinal);
            foreach (var task in node.Tasks)
                if (!tasks.Add(task.Key))
                    throw new ArgumentException($"Duplicate task key '{node.Key}/{task.Key}'.");
        }
        foreach (var node in nodes)
        {
            var dependencies = new HashSet<string>(StringComparer.Ordinal);
            foreach (var dependency in node.Dependencies)
            {
                if (!dependencies.Add(dependency.Node))
                    throw new ArgumentException($"Node '{node.Key}' has duplicate dependency '{dependency.Node}'.");
                if (!byKey.ContainsKey(dependency.Node))
                    throw new ArgumentException($"Node '{node.Key}' depends on unknown node '{dependency.Node}'.");
            }
        }

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var active = new HashSet<string>(StringComparer.Ordinal);
        var path = new List<string>();
        var stack = new Stack<(string Key, bool Exit)>();
        foreach (var node in nodes)
        {
            stack.Push((node.Key, false));
            while (stack.TryPop(out var frame))
            {
                if (frame.Exit)
                {
                    active.Remove(frame.Key);
                    path.RemoveAt(path.Count - 1);
                    continue;
                }
                if (active.Contains(frame.Key))
                    throw new ArgumentException("Dependency cycle: " +
                        string.Join(" -> ", path.Skip(path.IndexOf(frame.Key)).Append(frame.Key)) + ".");
                if (!visited.Add(frame.Key))
                    continue;
                active.Add(frame.Key);
                path.Add(frame.Key);
                stack.Push((frame.Key, true));
                foreach (var dependency in byKey[frame.Key].Dependencies.Reverse())
                    stack.Push((dependency.Node, false));
            }
        }
    }
}
