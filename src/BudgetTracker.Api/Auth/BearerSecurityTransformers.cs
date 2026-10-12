using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace BudgetTracker.Api.Auth;

/// <summary>Declares the bearer scheme in the OpenAPI document.</summary>
internal sealed class BearerSchemeTransformer : IOpenApiDocumentTransformer
{
    public const string SchemeName = "Bearer";

    public Task TransformAsync(
        OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[SchemeName] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            Description = "The opaque token returned by POST /auth/login, sent as 'Authorization: Bearer <token>'.",
        };
        return Task.CompletedTask;
    }
}

/// <summary>Marks every endpoint that requires authorization as needing the bearer scheme.</summary>
internal sealed class BearerRequirementTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(
        OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        var requiresAuth = context.Description.ActionDescriptor.EndpointMetadata.OfType<IAuthorizeData>().Any();
        if (requiresAuth)
        {
            operation.Security ??= [];
            operation.Security.Add(new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference(BearerSchemeTransformer.SchemeName, context.Document)] = [],
            });
        }

        return Task.CompletedTask;
    }
}
