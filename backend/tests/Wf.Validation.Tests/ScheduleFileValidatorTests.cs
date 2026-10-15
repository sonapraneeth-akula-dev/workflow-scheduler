using System.Text;
using System.Text.Json;
using Wf.Contracts.Definition;
using Wf.Sdk.Testing;
using Wf.Validation.Schedules;

namespace Wf.Validation.Tests;

public sealed class ScheduleFileValidatorTests
{
    [Fact]
    public void ScheduleFileCategorisedDefaultsThenOverridesPreserveWorkflowDefaults()
    {
        var schemas = new[] {
            new VariableSchema("env", VariableType.String, @default: Json("\"prod\"")),
            new VariableSchema("count", VariableType.Int, @default: Json("1")),
            new VariableSchema("enabled", VariableType.Bool, @default: Json("true"))
        };
        var result = Validate("""
            {
              "defaults":{"deployment":{"env":"dev"},"load":{"count":2}},
              "instances":[
                {"id":"smoke","cron":"0 9 * * *","timezone":"America/New_York","variables":{"env":"ppe"}},
                {"id":"load","interval":"00:05:00","timezone":"Etc/UTC","variables":{"count":3}}
              ]
            }
            """, schemas);
        Assert.True(result.IsValid);
        Assert.Equal("ppe", result.Instances[0].Variables["env"].GetString());
        Assert.Equal(2, result.Instances[0].Variables["count"].GetInt32());
        Assert.True(result.Instances[0].Variables["enabled"].GetBoolean());
        Assert.Equal("dev", result.Instances[1].Variables["env"].GetString());
        Assert.Equal(3, result.Instances[1].Variables["count"].GetInt32());
        Assert.Equal(TimeSpan.FromMinutes(5), result.Instances[1].Expression.Interval);
        Assert.Equal("prod", schemas[0].Default!.Value.GetString());
        Assert.True(Assert.IsType<bool>(VariableBinder.Bind(schemas, result.Instances[0].Variables)["enabled"]));
    }

    [Theory]
    [InlineData(VariableType.String, "\"hello\"")]
    [InlineData(VariableType.Int, "2147483647")]
    [InlineData(VariableType.Long, "9223372036854775807")]
    [InlineData(VariableType.Double, "2.5")]
    [InlineData(VariableType.Bool, "true")]
    [InlineData(VariableType.Enum, "\"dev\"")]
    [InlineData(VariableType.StringList, "[\"a\",\"b\"]")]
    [InlineData(VariableType.Duration, "\"01:02:03\"")]
    [InlineData(VariableType.Secret, "{\"Name\":\"deployment-token\"}")]
    public void ScheduleFileAcceptsAllVariableTypes(VariableType type, string value)
    {
        var schema = new VariableSchema("value", type, true,
            allowedValues: type == VariableType.Enum ? ["dev", "ppe"] : []);
        var result = Validate(WithVariables($$"""{"value":{{value}}}"""), [schema]);
        Assert.True(result.IsValid);
        Assert.Single(result.Instances);
        Assert.Single(VariableBinder.Bind([schema], result.Instances[0].Variables));
    }

    [Theory]
    [InlineData(VariableType.String, "12")]
    [InlineData(VariableType.Int, "2147483648")]
    [InlineData(VariableType.Long, "9223372036854775808")]
    [InlineData(VariableType.Double, "1e400")]
    [InlineData(VariableType.Bool, "\"true\"")]
    [InlineData(VariableType.Enum, "\"not-allowed\"")]
    [InlineData(VariableType.StringList, "[\"a\",1]")]
    [InlineData(VariableType.Duration, "\"not-a-duration\"")]
    [InlineData(VariableType.Secret, "{\"Name\":\"ref\",\"Value\":\"DO-NOT-LEAK\"}")]
    [InlineData(VariableType.String, "\"\\u0000\"")]
    [InlineData(VariableType.StringList, "[\"\\u0000\"]")]
    [InlineData(VariableType.Secret, "{\"Name\":\"\\u0000\"}")]
    public void ScheduleFileRejectsEachInvalidVariableType(VariableType type, string value)
    {
        var schema = new VariableSchema("value", type,
            allowedValues: type == VariableType.Enum ? ["dev"] : []);
        var result = Validate(WithVariables($$"""{"value":{{value}}}"""), [schema]);
        var error = Assert.Single(result.Errors);
        Assert.Equal("WFV013", error.RuleId);
        Assert.Equal("$.instances[0].variables[\"value\"]", error.Path);
        Assert.DoesNotContain("DO-NOT-LEAK", error.ToString());
        Assert.Empty(result.Instances);
    }

    [Theory]
    [InlineData("""{"defaults":{"a":{"missing":1}},"instances":[]}""", "$.defaults[\"a\"][\"missing\"]")]
    [InlineData("""{"defaults":{"a":{"count":"bad"}},"instances":[]}""", "$.defaults[\"a\"][\"count\"]")]
    [InlineData("""{"defaults":{"a":{"count":1},"b":{"count":2}},"instances":[]}""", "$.defaults[\"b\"][\"count\"]")]
    [InlineData("""{"instances":[{"id":"x","cron":"* * * * *","timezone":"Etc/UTC","variables":{"missing":1}}]}""",
        "$.instances[0].variables[\"missing\"]")]
    public void ScheduleFileReportsPreciseDefaultAndOverrideErrors(string json, string path)
    {
        var result = Validate(json, [new("count", VariableType.Int)]);
        var error = Assert.Single(result.Errors);
        Assert.Equal(RuleCatalog.ScheduleVariable.CreateError("schedules.json", path), error);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void ScheduleFileDoesNotHideInvalidDefaultsBehindValidOverrides()
    {
        var result = Validate("""
            {"defaults":{"load":{"count":"DO-NOT-LEAK"}},
             "instances":[{"id":"x","cron":"* * * * *","timezone":"Etc/UTC","variables":{"count":2}}]}
            """, [new("count", VariableType.Int)]);
        Assert.Equal("$.defaults[\"load\"][\"count\"]", Assert.Single(result.Errors).Path);
        Assert.Empty(result.Instances);
    }

    [Fact]
    public void ScheduleFileRequiresValuesOnlyForActualInstances()
    {
        var schema = new VariableSchema("token", VariableType.Secret, true);
        Assert.True(Validate("""{"instances":[]}""", [schema]).IsValid);
        var result = Validate(WithVariables("{}"), [schema]);
        Assert.Equal("$.instances[0].variables[\"token\"]", Assert.Single(result.Errors).Path);
        Assert.True(Validate(WithVariables("""{"token":{"Name":"token-ref"}}"""), [schema]).IsValid);
    }

    [Theory]
    [InlineData("""{"id":"x","timezone":"Etc/UTC"}""")]
    [InlineData("""{"id":"x","cron":"* * * * *","interval":"00:01:00","timezone":"Etc/UTC"}""")]
    [InlineData("""{"id":"x","cron":"bad","timezone":"Etc/UTC"}""")]
    [InlineData("""{"id":"x","interval":"00:00:00","timezone":"Etc/UTC"}""")]
    [InlineData("""{"id":"x","interval":"-00:00:01","timezone":"Etc/UTC"}""")]
    [InlineData("""{"id":"x","interval":"00:00:00.0000001","timezone":"Etc/UTC"}""")]
    [InlineData("""{"id":"x","interval":"00:00:01.5","timezone":"Etc/UTC"}""")]
    [InlineData("""{"id":"x","interval":"24855.03:14:08","timezone":"Etc/UTC"}""")]
    [InlineData("""{"id":"x","cron":"* * * * *","timezone":"Eastern Standard Time"}""")]
    [InlineData("""{"id":"x","cron":"* * * * *","timezone":"Unknown/Zone"}""")]
    public void ScheduleFileUsesSharedExpressionValidation(string instance)
    {
        var result = Validate($$"""{"instances":[{{instance}}]}""");
        Assert.Equal(RuleCatalog.ScheduleExpression.CreateError("schedules.json", "$.instances[0]"),
            Assert.Single(result.Errors));
    }

    [Theory]
    [InlineData("00:00:01")]
    [InlineData("00:00:01.0000000")]
    [InlineData("24855.03:14:07")]
    public void ScheduleFileAcceptsWholeSecondStorageBoundaries(string interval) =>
        Assert.True(Validate($$"""{"instances":[{"id":"x","interval":"{{interval}}","timezone":"Etc/UTC"}]}""").IsValid);

    [Theory]
    [InlineData("not-json")]
    [InlineData("null")]
    [InlineData("""{"instances":[null]}""")]
    [InlineData("""{"defaults":{"category":null},"instances":[]}""")]
    [InlineData("""{"defaults":{" ":{}},"instances":[]}""")]
    [InlineData("""{"instances":[{"id":"x","cron":"* * * * *"}]}""")]
    [InlineData("""{"instances":[{"id":"x","cron":"* * * * *","timezone":"Etc/UTC","versionPin":""}]}""")]
    [InlineData("""{"instances":[{"id":"x","cron":"* * * * *","timezone":"Etc/UTC","interval":null}]}""")]
    [InlineData("""{"instances":[{"id":"x","cron":"* * * * *","timezone":"Etc/UTC","versionPin":null}]}""")]
    [InlineData("""{"instances":[],"unexpected":"DO-NOT-LEAK"}""")]
    [InlineData("""{"instances":[],"instances":[]}""")]
    [InlineData("""{"defaults":{"a":{"value":1,"value":2}},"instances":[]}""")]
    public void ScheduleFileRejectsMalformedOrAmbiguousInputWithStructuredErrors(string json)
    {
        var result = Validate(json, file: "nested/custom.json");
        Assert.False(result.IsValid);
        Assert.Empty(result.Instances);
        Assert.Contains(result.Errors, error => error.RuleId == "WFV011");
        Assert.All(result.Errors, error =>
        {
            Assert.Equal("nested/custom.json", error.File);
            Assert.StartsWith("$", error.Path);
            Assert.False(string.IsNullOrWhiteSpace(error.Hint));
            Assert.DoesNotContain("DO-NOT-LEAK", error.Message);
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("same")]
    [InlineData("\\u0000")]
    public void ScheduleFileIdsAreStableNonemptyAndUnique(string second)
    {
        var result = Validate($$"""
            {"instances":[
                {"id":"same","cron":"* * * * *","timezone":"Etc/UTC"},
                {"id":"{{second}}","cron":"* * * * *","timezone":"Etc/UTC"}]}
            """);
        Assert.Equal("WFV014", Assert.Single(result.Errors).RuleId);
    }

    [Fact]
    public void ScheduleFileErrorsAreDeterministicAndJsonPathsEscapeKeys()
    {
        var first = Validate("""{"defaults":{"z":{"b":1},"a":{"quote\"key":2}},"instances":[]}""");
        var second = Validate("""{"instances":[],"defaults":{"a":{"quote\"key":2},"z":{"b":1}}}""");
        Assert.Equal(first.Errors, second.Errors);
        Assert.Equal("$.defaults[\"a\"][\"quote\\u0022key\"]", first.Errors[0].Path);
    }

    [Fact]
    public void ScheduleFileCancellationAndCallerErrorsPropagate()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            ScheduleFileValidator.Validate("invalid"u8.ToArray(), [], cancellationToken: source.Token));
        Assert.Throws<ArgumentNullException>(() => ScheduleFileValidator.Validate("{}"u8.ToArray(), null!,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentException>(() => ScheduleFileValidator.Validate("{}"u8.ToArray(), [], file: "",
            cancellationToken: TestContext.Current.CancellationToken));
    }

    private static string WithVariables(string variables) => $$"""
        {"instances":[{"id":"instance","cron":"* * * * *","timezone":"Etc/UTC","variables":{{variables}}}]}
        """;

    private static ScheduleFileValidationResult Validate(string json, IEnumerable<VariableSchema>? schemas = null,
        string file = "schedules.json") => ScheduleFileValidator.Validate(Encoding.UTF8.GetBytes(json),
            schemas ?? [], file, TestContext.Current.CancellationToken);

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

[Collection("Validator")]
public sealed class ScheduleFileValidatorIntegrationTests
{
    [Fact]
    public async Task ScheduleFileSharedValidatorAndTestingFacadeValidateRealPublishedWorkflow()
    {
        using var fixture = new ValidatorFixture();
        fixture.WriteWorkflow();
        var path = Path.Combine(fixture.Source, "schedules.json");
        await File.WriteAllTextAsync(path, """
            {"defaults":{"deployment":{"environment":"ppe"}},
             "instances":[{"id":"smoke","cron":"* * * * *","timezone":"Etc/UTC"}]}
            """, ValidatorFixture.Token);
        var result = await Validator.ValidateAsync(fixture.Source, fixture.Cache, fixture.AdditionalInputs,
            ValidatorFixture.Token);
        Assert.True(result.IsValid);
        Assert.NotNull(result.Build);
        var parsed = ScheduleFileValidator.Validate(await File.ReadAllBytesAsync(path, ValidatorFixture.Token),
            result.Build.Manifest.Variables, cancellationToken: ValidatorFixture.Token);
        Assert.Equal("ppe", parsed.Instances[0].Variables["environment"].GetString());
        await File.WriteAllTextAsync(path, """
            {"instances":[{"id":"bad","cron":"* * * * *","timezone":"Etc/UTC",
              "variables":{"unknown":"DO-NOT-LEAK"}}]}
            """, ValidatorFixture.Token);
        var direct = await Validator.ValidateAsync(fixture.Source, fixture.Cache, fixture.AdditionalInputs,
            ValidatorFixture.Token);
        var facade = await WorkflowValidation.ValidateAsync(fixture.Source, fixture.Cache, fixture.AdditionalInputs,
            ValidatorFixture.Token);
        Assert.Equal(direct.Errors, facade.Errors);
        Assert.Equal("WFV013", Assert.Single(direct.Errors).RuleId);
        Assert.DoesNotContain("DO-NOT-LEAK", direct.Errors[0].Message);
    }
}
