namespace Wf.ControlPlane.Api;

public static class OpenApiSetup
{
    public static IServiceCollection AddWorkflowOpenApi(this IServiceCollection services)
    {
        services.AddOpenApi("v1", options => options.ShouldInclude = description => description.GroupName is null or "v1");
        services.AddOpenApi("ui", options => options.ShouldInclude = description => description.GroupName == "ui");
        return services;
    }

    public static IEndpointRouteBuilder MapWorkflowOpenApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapOpenApi().RequireAuthorization();
        return endpoints;
    }
}
