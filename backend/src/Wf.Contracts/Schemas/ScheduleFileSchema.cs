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
        properties["versionPin"]!["maxLength"] = 256;
        properties["versionPin"]!["description"] =
            "Exact SemVer or version UUID, comparison ranges joined by spaces (AND) or || (OR), or *, major.* and major.minor.* wildcards.";
        properties["name"] = NameSchema();
        properties["interval"]!["type"] = "string";
        properties["interval"]!["pattern"] = "^(\\d+\\.)?\\d{2}:\\d{2}:\\d{2}(\\.0{1,7})?$";
        properties["overlap"]!["default"] = nameof(ScheduleOverlapPolicy.Skip);
        properties["catchUp"]!["default"] = nameof(ScheduleCatchUpPolicy.Latest);
        instance["oneOf"] = new JsonArray(
            new JsonObject { ["required"] = new JsonArray("cron"), ["not"] = new JsonObject
                { ["required"] = new JsonArray("interval") } },
            new JsonObject { ["required"] = new JsonArray("interval"), ["not"] = new JsonObject
                { ["required"] = new JsonArray("cron") } });
        var cards = schema["properties"]!["historyCards"]!;
        cards["type"] = "array";
        cards["maxItems"] = 32;
        cards["description"] = "Ordered Service History cards. Omit for one all-runs workflow card; [] hides all cards. References use instance IDs; schedule-based manual runs remain in their schedule's card.";
        var card = cards["items"]!;
        card["properties"]!["id"] = new JsonObject
        {
            ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 128,
            ["pattern"] = "^[A-Za-z0-9][A-Za-z0-9_-]*$"
        };
        card["properties"]!["name"] = NameSchema();
        var references = card["properties"]!["scheduleIds"]!;
        references["minItems"] = 1;
        references["maxItems"] = 32;
        references["uniqueItems"] = true;
        references["items"]!["minLength"] = 1;
        return schema;
    }

    private static JsonObject NameSchema() => new()
    {
        ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 200,
        ["pattern"] = "^(?![\\s\\S]*[\\u0000-\\u001f\\u007f-\\u009f])\\S(?:[\\s\\S]*\\S)?$"
    };
}
