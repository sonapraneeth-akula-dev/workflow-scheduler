using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Wf.Contracts.Definition;
using Wf.Contracts.Manifest;

namespace Wf.ControlPlane.Api;

public sealed record WorkflowVariablesQuery([property: FromQuery(Name = "version")] string? Version = null);

public sealed record WorkflowVariablesResponse(string WorkflowId, Guid VersionId, string Version,
    WorkflowVariable[] Variables)
{
    public static WorkflowVariablesResponse From(Guid id, WorkflowManifest manifest)
    {
        if (manifest.Variables.IsDefault || manifest.Variables.Any(variable => variable is null) ||
            manifest.Variables.Select(variable => variable.Key).Distinct(StringComparer.Ordinal).Count() != manifest.Variables.Length)
            throw new InvalidDataException();
        return new(manifest.Name, id, manifest.Version, manifest.Variables.Select(variable =>
        {
            if (variable.Default is { } value) variable.Validate(value);
            return new WorkflowVariable(variable.Key, variable.Type, variable.Required,
                variable.Default, variable.AllowedValues.ToArray());
        }).ToArray());
    }
}

public sealed record WorkflowVariable(string Key,
    [property: JsonConverter(typeof(JsonStringEnumConverter<VariableType>))] VariableType Type, bool Required, JsonElement? Default,
    string[] AllowedValues);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(WorkflowVariablesResponse))]
public partial class VariablesJsonContext : JsonSerializerContext;
