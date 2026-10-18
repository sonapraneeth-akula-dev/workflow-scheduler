using System.Collections.Immutable;
using System.Text.Json;
using Wf.Contracts.Definition;

namespace Wf.Validation;

public static class SecretRefValidator
{
    public static ImmutableArray<ValidationError> Validate(JsonElement value,
        string file = "workflow.cs", string path = "$") =>
        SecretReference.IsValidValue(value) ? [] : [RuleCatalog.VariableType.CreateError(file, path)];
}
