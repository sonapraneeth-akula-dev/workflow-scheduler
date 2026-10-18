using System.Collections.Immutable;
using Wf.Contracts.Definition;
using Wf.Validation;

namespace Wf.Sdk;

public static class DagValidator
{
    public static void Validate(ImmutableArray<NodeDefinition> nodes) => DagRules.Validate(nodes);
}
