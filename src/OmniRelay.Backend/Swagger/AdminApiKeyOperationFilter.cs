using OmniRelay.Backend.Security;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace OmniRelay.Backend.Swagger;

public sealed class AdminApiKeyOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var isAdminEndpoint = context.ApiDescription.ActionDescriptor.EndpointMetadata
            .Any(x => x is AdminEndpointMetadata);

        if (!isAdminEndpoint)
        {
            return;
        }

        operation.Security ??= [];
        operation.Security.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "AdminApiKey"
                }
            }] = []
        });
    }
}
