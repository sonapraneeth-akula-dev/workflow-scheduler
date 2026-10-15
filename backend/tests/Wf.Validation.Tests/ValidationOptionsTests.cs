using Wf.Sdk.Testing;
using Wf.Validation.Rules;

namespace Wf.Validation.Tests;

public sealed class ValidationOptionsTests
{
    [Fact]
    public void Default_type_quota_uses_the_same_shared_rule_default() =>
        Assert.Equal(TeamRegistryRules.DefaultTypeQuota, new ValidationOptions().TypeQuota);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public async Task Invalid_trusted_quota_is_rejected_by_all_entrypoints_before_discovering_files(int quota)
    {
        var missing = Path.Combine(Path.GetTempPath(), "wf-quota-absent-" + Guid.NewGuid().ToString("N"));
        var options = new ValidationOptions(quota);
        var token = TestContext.Current.CancellationToken;
        Assert.Throws<ArgumentOutOfRangeException>(() => Validator.Validate(missing, cancellationToken: token, options: options));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Validator.ValidateAsync(missing, cancellationToken: token, options: options));
        Assert.Throws<ArgumentOutOfRangeException>(() => WorkflowValidation.Validate(missing, cancellationToken: token, options: options));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => WorkflowValidation.ValidateAsync(missing, cancellationToken: token, options: options));
    }

    [Fact]
    public async Task Positive_options_do_not_hide_the_normal_missing_directory_diagnostic()
    {
        var missing = Path.Combine(Path.GetTempPath(), "wf-quota-absent-" + Guid.NewGuid().ToString("N"));
        var options = new ValidationOptions(11);
        var direct = await Validator.ValidateAsync(missing, cancellationToken: TestContext.Current.CancellationToken, options: options);
        var facade = await WorkflowValidation.ValidateAsync(missing, cancellationToken: TestContext.Current.CancellationToken, options: options);
        Assert.Equal([RuleCatalog.DirectoryNotFound.CreateError(".", "$")], direct.Errors);
        Assert.Equal(direct.Errors, facade.Errors);
    }
}
