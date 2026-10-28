using FCG.Catalog.Api.Contracts;
using FCG.Catalog.Api.Controllers;
using FCG.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace FCG.Catalog.Api.Documentation;

public sealed class PurchaseOpenApiFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (context.MethodInfo.DeclaringType != typeof(PurchasesController)) return;
        operation.Responses!["409"] = new OpenApiResponse
        {
            Description = "A pending order includes orderId/location; an acquired game returns game_already_acquired.",
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["application/problem+json"] = new()
                {
                    Schema = new OpenApiSchema
                    {
                        OneOf = [context.SchemaGenerator.GenerateSchema(typeof(PendingOrderConflict), context.SchemaRepository),
                            context.SchemaGenerator.GenerateSchema(typeof(ProblemDetails), context.SchemaRepository)]
                    }
                }
            }
        };
        if (operation.Responses["202"] is OpenApiResponse accepted)
            accepted.Headers = new Dictionary<string, IOpenApiHeader>
            { ["Location"] = new OpenApiHeader { Description = "The caller's order resource.", Schema = new OpenApiSchema { Type = JsonSchemaType.String } } };
    }
}

public sealed class PurchaseBodySchemaFilter : ISchemaFilter
{
    public void Apply(IOpenApiSchema schema, SchemaFilterContext context)
    {
        if (context.Type == typeof(PurchaseBody) && schema is OpenApiSchema body) body.AdditionalPropertiesAllowed = false;
    }
}
