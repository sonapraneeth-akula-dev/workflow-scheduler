using Wf.Contracts;
using Wf.Contracts.Definition;

namespace Wf.Sdk.Tests;

public sealed class TaskContextSecretsTests
{
    [Fact]
    public async Task Secret_values_are_ephemeral_task_context_data_not_variable_substitution()
    {
        var reference = new SecretReference("secret://local/token");
        var variables = new Dictionary<string, object> { ["credential"] = reference };
        var context = new TaskContext(variables: variables, secrets:
            new Dictionary<string, string> { [reference.Name] = "ephemeral-value" });
        var workflow = Workflow.Define("tests/context/secrets", "1", workflow => workflow.Type("test")
            .Variables(builder => builder.Secret("credential", reference.Name))
            .Node("work", node => node.Task("read", task =>
            {
                Assert.Equal("work/read", task.TaskKey);
                Assert.Equal(reference, task.Var<SecretReference>("credential"));
                Assert.Equal("ephemeral-value", task.Secret(task.Var<SecretReference>("credential")));
                Assert.Equal("ephemeral-value", task.Secret(reference.Name));
            })));
        await new TaskRegistry(workflow).ExecuteAsync("work/read", context);
        Assert.Equal(reference, context.Var("credential"));
        var manifest = ManifestWriter.Write(workflow);
        Assert.Contains(reference.Name, manifest);
        Assert.DoesNotContain("ephemeral-value", manifest);
    }

    [Theory]
    [InlineData("token")]
    [InlineData("secret://local/token")]
    [InlineData("../traversal")]
    [InlineData("secret://local/a\\b")]
    public void Missing_or_invalid_reference_is_a_safe_nonretryable_ValidationError(string reference)
    {
        var error = Assert.Throws<TaskFailureException>(() => new TaskContext().Secret(reference));
        Assert.False(error.Retryable);
        Assert.Equal(FailureCategory.ValidationError, error.Category);
        Assert.Null(error.InnerException);
    }
}
