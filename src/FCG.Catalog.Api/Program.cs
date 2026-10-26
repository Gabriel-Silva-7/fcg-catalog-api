using FCG.Catalog.Api;
using FCG.Catalog.Api.Documentation;
using FCG.Catalog.Api.Errors;
using FCG.Catalog.Api.Security;
using FCG.Catalog.Application.Catalog;
using FCG.Catalog.Application.Common;
using FCG.Catalog.Infrastructure.Catalog;
using FCG.Catalog.Infrastructure.Persistence;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.OpenApi;

if (args is ["--healthcheck"])
    return await ContainerProbe.RunAsync(new Uri("http://127.0.0.1:8080/health/ready"));

var builder = WebApplication.CreateBuilder(args);
var connection = builder.Configuration.GetConnectionString("Default");
if (string.IsNullOrWhiteSpace(connection)) throw new InvalidOperationException("ConnectionStrings:Default is required for Catalog.");
builder.Services.AddDbContext<CatalogDbContext>(options => options.UseNpgsql(connection));
builder.Services.AddScoped<IGameRepository, GameRepository>();
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddScoped<CreateGameHandler>();
builder.Services.AddScoped<ListGamesHandler>();
builder.Services.AddScoped<GetGameHandler>();
builder.Services.AddCatalogAuthentication(builder.Configuration);
builder.Services.AddControllers();
builder.Services.AddApiProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo { Title = "FCG Catalog API", Version = "v1", Description = "Public game catalog and administrative creation." });
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT" });
    options.OperationFilter<CatalogSecurityOperationFilter>();
    options.SchemaFilter<CatalogPriceSchemaFilter>();
});
builder.Services.AddHealthChecks().AddCheck<CatalogDatabaseHealthCheck>("catalog-database", tags: ["ready"]);
var app = builder.Build();
// Explicit opt-in for local setup/tests only; normal startup does not change the schema.
if (builder.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<CatalogDbContext>().Database.MigrateAsync();
}
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();
if (builder.Configuration.GetValue<bool>("Swagger:Enabled"))
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
app.MapControllers();
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResultStatusCodes = { [HealthStatus.Degraded] = 503, [HealthStatus.Unhealthy] = 503 }
});
app.Run();
return 0;

public partial class Program;
