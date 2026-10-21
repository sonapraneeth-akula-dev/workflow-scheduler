using System.Text.Json;
using System.Text.Json.Serialization;
using Wf.ControlPlane.Auth;
using Wf.ControlPlane.Hosting;

namespace Wf.ControlPlane.Api.Admin;

public static class KubernetesMemoryEndpoints
{
    public static IEndpointRouteBuilder MapKubernetesMemoryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/admin/system/kubernetes-memory", Read).RequireAuthorization(AuthExtensions.Admin)
            .Produces<KubernetesMemorySnapshot>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
            .ProducesProblem(503)
            .WithDescription("Global Admin only. Read-only Kubernetes node and application-pod RAM working sets. " +
                "Measurements come from metrics.k8s.io; missing samples are null, never fabricated zero. " +
                "Separate from application heartbeats; unavailable/unauthorized cluster monitoring returns 503.");
        return endpoints;
    }

    private static async Task<IResult> Read(HttpContext context, KubernetesObserver memory, ILoggerFactory logging)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        if (context.Request.Query.Count != 0)
            return Results.Problem(statusCode: 400, title: "Kubernetes RAM inventory does not accept query parameters");
        if (!memory.Enabled)
            return Results.Problem(statusCode: 503, title: "Kubernetes RAM monitoring is not configured on this API instance");
        try
        {
            return Results.Json(await memory.ReadAsync(context.RequestAborted),
                KubernetesMemoryJson.Default.KubernetesMemorySnapshot);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException or
            KeyNotFoundException or InvalidOperationException or FormatException or OverflowException or ArgumentException ||
            error is OperationCanceledException && !context.RequestAborted.IsCancellationRequested)
        {
            logging.CreateLogger("Wf.KubernetesMemory").LogError(error, "Cannot read Kubernetes RAM observations.");
            return Results.Problem(statusCode: 503, title: "Kubernetes RAM monitoring unavailable",
                detail: "Check the Metrics API, projected service-account token, CA trust and read-only pod/node permissions.");
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(KubernetesMemorySnapshot))]
public partial class KubernetesMemoryJson : JsonSerializerContext;
