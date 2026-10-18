using Wf.ControlPlane.Registry;

namespace Wf.ControlPlane.Tests.Registry;

public sealed class WorkflowEnvironmentPolicyTests
{
    [Theory]
    [InlineData("demo", "Demo")]
    [InlineData("dev", "Demo")]
    [InlineData("test", "Demo")]
    [InlineData("prod", "Production")]
    [InlineData("ppe", "Production")]
    public void Only_the_matching_deployment_is_allowed(string environment, string deployment)
    {
        var policy = new WorkflowEnvironmentPolicy(environment);
        Assert.Equal(deployment, policy.Deployment);
        Assert.True(policy.Allows(deployment));
        Assert.False(policy.Allows(deployment == "Demo" ? "Production" : "Demo"));
        Assert.False(policy.Allows("unknown"));
        Assert.Equal(deployment == "Production", policy.Allows(null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("development")]
    [InlineData("staging")]
    [InlineData("Prod")]
    public void Unknown_environments_fail_closed(string environment) =>
        Assert.Throws<InvalidOperationException>(() => new WorkflowEnvironmentPolicy(environment));
}
