using FCG.Catalog.Api.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace FCG.Catalog.Api.Documentation;

public sealed class CatalogSecurityOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var metadata = context.ApiDescription.ActionDescriptor.EndpointMetadata;
        if (metadata.OfType<IAllowAnonymous>().Any() || !metadata.OfType<IAuthorizeData>().Any()) return;
        operation.Security = [new OpenApiSecurityRequirement {
            [new OpenApiSecuritySchemeReference("Bearer", context.Document)] = [] }];
    }
}

public sealed class CatalogPriceSchemaFilter : ISchemaFilter
{
    public void Apply(IOpenApiSchema schema, SchemaFilterContext context)
    {
        if ((context.Type != typeof(CreateGameRequest) && context.Type != typeof(UpdateGameRequest)) || schema.Properties is null
            || !schema.Properties.TryGetValue("basePrice", out var price) || price is not OpenApiSchema mutablePrice) return;
        mutablePrice.Type = JsonSchemaType.Number;
        mutablePrice.Minimum = "0";
        mutablePrice.Maximum = "9999999999999999.99";
        mutablePrice.Description = "Required nonnegative price with exact cents; fractions of a cent are rejected, never rounded.";
    }
}
