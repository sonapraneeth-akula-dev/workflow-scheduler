using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using Wf.Contracts.Schedules;

namespace Wf.Contracts.Schemas;

/// <summary>Editor schema generated from the same source-generated contract used by the server.</summary>
public static class ScheduleFileSchema
{
    public static JsonNode Export()
    {
        var schema = ScheduleFileJsonContext.Default.ScheduleFile.GetJsonSchemaAsNode(
            new JsonSchemaExporterOptions { TreatNullObliviousAsNonNullable = true });
        schema["$schema"] = "https://json-schema.org/draft/2020-12/schema";
        schema["title"] = "Kardia code-defined schedules";
        var instance = schema["properties"]!["instances"]!["items"]!;
        var properties = instance["properties"]!;
        foreach (var key in new[] { "id", "timezone", "cron", "versionPin" })
            properties[key] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["pattern"] = "\\S" };
        properties["interval"]!["type"] = "string";
        properties["interval"]!["pattern"] = "^(\\d+\\.)?\\d{2}:\\d{2}:\\d{2}(\\.0{1,7})?$";
        properties["overlap"]!["default"] = nameof(ScheduleOverlapPolicy.Skip);
        properties["catchUp"]!["default"] = nameof(ScheduleCatchUpPolicy.Latest);
        instance["oneOf"] = new JsonArray(
            new JsonObject { ["required"] = new JsonArray("cron"), ["not"] = new JsonObject
                { ["required"] = new JsonArray("interval") } },
            new JsonObject { ["required"] = new JsonArray("interval"), ["not"] = new JsonObject
                { ["required"] = new JsonArray("cron") } });
        return schema;
    }
}
