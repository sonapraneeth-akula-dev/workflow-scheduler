using Microsoft.OpenApi;
using Wf.ControlPlane.Live;

namespace Wf.ControlPlane.Api;

public static class OpenApiSetup
{
    public static IServiceCollection AddWorkflowOpenApi(this IServiceCollection services)
    {
        services.AddOpenApi("v1", options =>
        {
            options.ShouldInclude = description => description.GroupName is null or "v1";
            options.AddSchemaTransformer((schema, context, _) =>
            {
                // Transformer-generated nested object schemas otherwise lose the nullable abort annotation.
                if (context.JsonTypeInfo.Type == typeof(LiveAbort) && context.JsonPropertyInfo is { IsGetNullable: true })
                    schema.Type |= JsonSchemaType.Null;
                return Task.CompletedTask;
            });
            options.AddDocumentTransformer(async (document, context, token) =>
            {
                foreach (var type in new[] { typeof(RunDelta), typeof(ListDelta), typeof(LiveResync),
                    typeof(LiveEvent), typeof(LiveAbort), typeof(LiveRunSummary), typeof(LiveSubscriptionProblem),
                    typeof(ReportAdded) })
                    document.AddComponent(type.Name, await context.GetOrCreateSchemaAsync(type, null, token));
            });
        });
        services.AddOpenApi("ui", options => options.ShouldInclude = description => description.GroupName == "ui");
        return services;
    }

    public static IEndpointRouteBuilder MapWorkflowOpenApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapOpenApi().RequireAuthorization();
        return endpoints;
    }
}
