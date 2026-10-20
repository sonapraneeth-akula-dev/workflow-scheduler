using System.Text.Json;
using Wf.Contracts;
using Wf.Contracts.Definition;
using Wf.ControlPlane.Api;
using Wf.Engine.Core;

namespace Wf.ControlPlane.Tests;

public sealed class FailureCatalogTests
{
    private static FailureCatalog Catalog() => FailureCatalog.Load(Path.Combine(AppContext.BaseDirectory, "failure-categories"));

    [Fact]
    public void Shared_infra_rules_win_without_changing_typed_retry_classification()
    {
        var error = new TaskFailureException(
            "Synthetic infrastructure network failure; Synthetic required validation failure.",
            category: FailureCategory.TaskError);
        Assert.Equal(new("Infra", "Network", "system-infra/demo-network"),
            Catalog().Classify("demo", error.Message, error.Category));
        Assert.Equal(FailureCategory.TaskError, FailureClassifier.Classify(error));
    }

    [Fact]
    public void Team_rules_are_literal_case_insensitive_and_never_cross_team_boundaries()
    {
        const string message = "SYNTHETIC REQUIRED VALIDATION FAILURE: order rejected.";
        Assert.Equal(new("Service", "Validation", "demo/required-validation"),
            Catalog().Classify("demo", message, FailureCategory.TaskError));
        Assert.Equal(new("Service", "Unclassified", null),
            Catalog().Classify("other", message, FailureCategory.TaskError));
        Assert.Equal(new("Service", "Unclassified", null),
            Catalog().Classify("demo", ".*validation.*", FailureCategory.TaskError));
    }

    [Theory]
    [InlineData(FailureCategory.AgentLost)]
    [InlineData(FailureCategory.PlatformError)]
    [InlineData(FailureCategory.ResourceLimit)]
    public void Unmatched_typed_infrastructure_failures_keep_their_domain_without_inventing_a_message_match(FailureCategory category)
    {
        Assert.Equal(new("Infra", "Unclassified", null), Catalog().Classify("demo", null, category));
        Assert.Equal(new("Infra", "Unclassified", null), Catalog().Classify("demo", "unmapped reason", category));
    }

    [Fact]
    public void Typed_placement_failures_explain_the_scheduler_cause_without_a_message_rule()
    {
        Assert.Equal(new("Infra", "Placement", null), Catalog().Classify("demo", null, FailureCategory.Placement));
        Assert.Equal(new("Infra", "Placement", null),
            Catalog().Classify("demo", "No eligible healthy worker became available.", FailureCategory.Placement));
    }

    [Fact]
    public void Regression_demo_failures_and_actual_timeouts_keep_explicit_domains_categories_and_rules()
    {
        Assert.Equal(new("Infra", "Network", "system-infra/regression-network"),
            Catalog().Classify("data-science", "Regression sample infrastructure failure: feature store connection refused.",
                FailureCategory.PlatformError));
        Assert.Equal(new("Service", "Validation", "data-science/regression-validation"),
            Catalog().Classify("data-science", "Regression sample service validation failure: quality gate rejected training.",
                FailureCategory.ValidationError));
        Assert.Equal(new("Service", "Timeout", null), Catalog().Classify("data-science", null, FailureCategory.Timeout));
        Assert.Equal(new("Service", "Timeout", null), Catalog().Classify("data-science", "Task execution was cancelled.", FailureCategory.Timeout));
        Assert.Equal(new("Service", "Unclassified", null),
            Catalog().Classify("another-team", "Regression sample service validation failure.", FailureCategory.ValidationError));
    }

    [Theory]
    [InlineData("""{"rules":[null]}""")]
    [InlineData("""{"rules":null}""")]
    [InlineData("""{"rules":[{"id":"duplicate","contains":"first","category":"One"},{"id":"duplicate","contains":"second","category":"Two"}]}""")]
    [InlineData("""{"rules":[{"id":"a","contains":" ","category":"One"}]}""")]
    [InlineData("""{"rules":[{"id":"a","contains":"value","category":""}]}""")]
    [InlineData("""{"rules":[{"id":"a","contains":"value","category":"One","type":"Infra"}]}""")]
    [InlineData("""{"rules":[{"id":"a","category":"One"}]}""")]
    [InlineData("""{"rules":[],"rules":[]}""")]
    public void Invalid_catalog_configuration_fails_explicitly_instead_of_silently_disabling_rules(string json)
    {
        var root = Path.Combine(Path.GetTempPath(), "wf-failure-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "system-infra"));
        Directory.CreateDirectory(Path.Combine(root, "teams"));
        try
        {
            File.WriteAllText(Path.Combine(root, "system-infra", "rules.json"), json);
            var error = Record.Exception(() => FailureCatalog.Load(root));
            Assert.True(error is JsonException or InvalidDataException, $"Expected a clear configuration error; got {error}.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Summaries_preserve_mixed_categories_and_recovered_failures_without_overriding_terminal_root_cause()
    {
        var recovered = new RunFailureDetail("Infra", "Network", "connection refused", "fetch", "fetch/request",
            1, "Recovered", "TaskError", "system-infra/connection-refused", true);
        var terminal = new RunFailureDetail("Service", "Validation", "order rejected", "validate", "validate/total",
            1, "Failed", "ValidationError", "demo/required-validation", true);
        var summary = RunFailures.Summarize([recovered, terminal, recovered with { Attempt = 2 }])!;
        Assert.Equal(3, summary.Count);
        Assert.Equal(2, summary.RecoveredCount);
        Assert.Equal(terminal, summary.Primary);
        Assert.Collection(summary.Groups,
            group => Assert.Equal(new("Service", "Validation", "Failed", 1), group),
            group => Assert.Equal(new("Infra", "Network", "Recovered", 2), group));
        Assert.Null(RunFailures.Summarize([]));
        Assert.Throws<InvalidDataException>(() => RunFailures.Summarize([terminal with { Impact = "unknown" }]));
        Assert.Throws<InvalidDataException>(() => Catalog().Classify("demo", "unmapped", (FailureCategory)999));
    }
}
