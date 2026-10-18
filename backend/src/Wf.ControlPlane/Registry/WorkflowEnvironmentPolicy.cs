namespace Wf.ControlPlane.Registry;

public sealed class WorkflowEnvironmentPolicy
{
    public WorkflowEnvironmentPolicy(string environment)
    {
        Deployment = environment switch
        {
            "demo" or "dev" or "test" => "Demo",
            "prod" or "ppe" => "Production",
            _ => throw new InvalidOperationException(
                "WF_WORKFLOW_ENVIRONMENT must be demo, dev, test, prod or ppe.")
        };
    }

    public string Deployment { get; }

    public bool Allows(string? deployment) => (deployment ?? "Production") == Deployment;
}
