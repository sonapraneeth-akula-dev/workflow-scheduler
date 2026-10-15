using System.Text.Json;
using System.Text.Json.Nodes;
using Wf.Contracts.Schedules;
using Wf.Contracts.Schemas;

namespace Wf.Validation.Tests;

public sealed class ScheduleSchemaTests
{
    [Fact]
    public void PublishedSchemaMatchesExportedContract()
    {
        var schema = JsonNode.Parse(File.ReadAllText(Path.Combine(BackendRoot(),
            "src", "Wf.Contracts", "Schemas", "schedules.schema.json")));
        Assert.True(JsonNode.DeepEquals(schema, ScheduleFileSchema.Export()));
        var instance = schema!["properties"]!["instances"]!["items"]!;
        Assert.Contains("id", instance["required"]!.AsArray().Select(node => node!.GetValue<string>()));
        Assert.Contains("timezone", instance["required"]!.AsArray().Select(node => node!.GetValue<string>()));
        Assert.Equal(2, instance["oneOf"]!.AsArray().Count);
        Assert.False(instance["additionalProperties"]!.GetValue<bool>());
    }

    [Fact]
    public void SampleAcceptsCategorisedDefaultsAndBothExpressions()
    {
        var json = File.ReadAllText(Path.Combine(BackendRoot(), "samples", "workflows",
            "scenario-a.schedules.json"));
        var file = JsonSerializer.Deserialize(json, ScheduleFileJsonContext.Default.ScheduleFile)!;
        Assert.Equal("dev", file.Defaults["deployment"]["env"].GetString());
        Assert.Equal(2, file.Instances.Length);
        Assert.Equal("daily-dev", file.Instances[0].Id);
        Assert.Equal("America/New_York", file.Instances[0].Timezone);
        Assert.Equal(TimeSpan.FromMinutes(15), file.Instances[1].Interval);
        Assert.Equal("ppe", file.Instances[1].Variables["env"].GetString());
        var roundTrip = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(file, ScheduleFileJsonContext.Default.ScheduleFile),
            ScheduleFileJsonContext.Default.ScheduleFile)!;
        Assert.Equal(JsonSerializer.Serialize(file, ScheduleFileJsonContext.Default.ScheduleFile),
            JsonSerializer.Serialize(roundTrip, ScheduleFileJsonContext.Default.ScheduleFile));
    }

    [Theory]
    [InlineData("""{"instances":[{"cron":"* * * * *","timezone":"Etc/UTC"}]}""")]
    [InlineData("""{"instances":[{"id":"daily","cron":"* * * * *"}]}""")]
    [InlineData("""{"instances":[{"id":null,"cron":"* * * * *","timezone":"Etc/UTC"}]}""")]
    [InlineData("""{"instances":[{"id":"daily","cron":"* * * * *","timezone":null}]}""")]
    [InlineData("""{}""")]
    [InlineData("""{"instances":[],"unknown":true}""")]
    [InlineData("""{"instances":[],"instances":[]}""")]
    [InlineData("""{"instances":[{"id":"daily","timezone":"Etc/UTC","overlap":42}]}""")]
    [InlineData("""{"instances":[{"id":"daily","timezone":"Etc/UTC","overlap":"Invalid"}]}""")]
    [InlineData("""{"instances":[{"id":"daily","timezone":"Etc/UTC","catchUp":"Invalid"}]}""")]
    [InlineData("""{"defaults":{"category":[]},"instances":[]}""")]
    [InlineData("""{"defaults":null,"instances":[]}""")]
    [InlineData("""{"instances":[{"id":"daily","timezone":"Etc/UTC","variables":null}]}""")]
    public void StructuralContractRejectsMissingOrInvalidFields(string json) =>
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize(json, ScheduleFileJsonContext.Default.ScheduleFile));

    [Fact]
    public void DefaultPoliciesAreSkipAndLatestOnly()
    {
        var file = JsonSerializer.Deserialize(
            """{"instances":[{"id":"daily","cron":"* * * * *","timezone":"Etc/UTC"}]}""",
            ScheduleFileJsonContext.Default.ScheduleFile)!;
        Assert.Equal(ScheduleOverlapPolicy.Skip, file.Instances[0].Overlap);
        Assert.Equal(ScheduleCatchUpPolicy.Latest, file.Instances[0].CatchUp);
        Assert.Empty(file.Defaults);
        Assert.Empty(file.Instances[0].Variables);
    }

    private static string BackendRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Wf.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Cannot locate backend root.");
    }
}
