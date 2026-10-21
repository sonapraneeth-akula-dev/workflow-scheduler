using System.Text.Json;
using System.Text.Json.Serialization;
using Wf.ControlPlane.Auth;
using Wf.ControlPlane.Hosting;

namespace Wf.ControlPlane.Api.Admin;

public static class DeploymentEndpoints
{
    public static IEndpointRouteBuilder MapDeploymentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/admin/system/deployments", Read).RequireAuthorization(AuthExtensions.Admin)
            .Produces<DeploymentSnapshot>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(503)
            .WithDescription("Global Admin read-only Kubernetes observations for the four Kardia workloads, their pods and recent " +
                "migration jobs. Target images and observed updated/ready counts are separate: OnDelete staging is not a " +
                "completed release. No environment variables, secrets, commands, logs or deployment controls are returned. " +
                "Lists are bounded; failed, truncated, disabled or unauthorized observation is 503, never healthy empty state.");
        return endpoints;
    }
    private static async Task<IResult> Read(HttpContext context, KubernetesObserver observer, ILoggerFactory logging)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        if (context.Request.Query.Count != 0)
            return Results.Problem(statusCode: 400, title: "Deployment inventory does not accept query parameters");
        if (!observer.Enabled)
            return Results.Problem(statusCode: 503, title: "Kubernetes deployment observation is not configured on this API instance");
        try
        {
            return Results.Json(await observer.ReadDeploymentsAsync(context.RequestAborted),
                DeploymentJson.Default.DeploymentSnapshot);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException or KeyNotFoundException or
            InvalidOperationException or FormatException or OverflowException or ArgumentException ||
            error is OperationCanceledException && !context.RequestAborted.IsCancellationRequested)
        {
            logging.CreateLogger("Wf.Deployments").LogError(error, "Cannot read Kubernetes deployment observations.");
            return Results.Problem(statusCode: 503, title: "Deployment observation unavailable",
                detail: "Check Kubernetes connectivity, projected CA/token and read-only workload, pod and job permissions.");
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DeploymentSnapshot))]
public partial class DeploymentJson : JsonSerializerContext;
