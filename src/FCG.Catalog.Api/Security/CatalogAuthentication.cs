using System.Security.Cryptography;
using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using FCG.Contracts;
using FCG.Catalog.Api.Errors;
using FCG.Catalog.Application.Identity;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace FCG.Catalog.Api.Security;

public static class CatalogAuthentication
{
    private static readonly object CurrentRole = new();
    private static readonly object StateUnavailable = new();
    public static IServiceCollection AddCatalogAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection("Jwt").Get<JwtContractOptions>() ?? new();
        options.Validate(isIssuer: false);
        var pem = File.ReadAllText(options.PublicKeyPath);
        var fields = PemEncoding.Find(pem);
        if (pem[fields.Label] != "PUBLIC KEY") throw new ArgumentException("Catalog requires a SubjectPublicKeyInfo PUBLIC KEY PEM.");
        using var rsa = RSA.Create(); rsa.ImportFromPem(pem);
        if (rsa.KeySize < 2048) throw new ArgumentException("JWT RSA public keys must contain at least 2048 bits.");
        var publicKey = new RsaSecurityKey(rsa.ExportParameters(false));
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme).Configure<TimeProvider>((jwt, clock) =>
        {
            jwt.MapInboundClaims = false; jwt.IncludeErrorDetails = false;
            jwt.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true, IssuerSigningKey = publicKey, RequireSignedTokens = true,
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256], ValidateIssuer = true, ValidIssuer = options.Issuer,
                ValidateAudience = true, ValidAudience = options.Audience, ValidateLifetime = true, RequireExpirationTime = true,
                ClockSkew = TimeSpan.Zero, NameClaimType = "sub", RoleClaimType = "role",
                LifetimeValidator = (notBefore, expires, _, _) => notBefore.HasValue && expires.HasValue && notBefore.Value < expires.Value
                    && notBefore.Value <= clock.GetUtcNow().UtcDateTime && clock.GetUtcNow().UtcDateTime < expires.Value
            };
            jwt.Events = new JwtBearerEvents
            {
                OnAuthenticationFailed = context =>
                {
                    context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Catalog.Identity")
                        .LogInformation("IdentityTokenRejected {TraceId} {ExceptionType}",
                            FCG.Catalog.Api.Diagnostics.TraceIdentity.Resolve(context.HttpContext), context.Exception.GetType().Name);
                    return Task.CompletedTask;
                },
                OnTokenValidated = async context =>
                {
                    var (header, payload) = context.SecurityToken switch
                    {
                        JsonWebToken token => (token.EncodedHeader, token.EncodedPayload),
                        JwtSecurityToken token => (token.RawHeader, token.RawPayload), _ => ("", "")
                    };
                    if (!HasUniqueMembers(header) || !HasUniqueMembers(payload)) { context.Fail("Ambiguous identity token."); return; }
                    var principal = context.Principal!; var subjects = principal.FindAll("sub").ToArray();
                    var roles = principal.FindAll("role").ToArray(); var ids = principal.FindAll("jti").ToArray();
                    if (subjects.Length != 1 || !Guid.TryParse(subjects[0].Value, out var userId) || userId == Guid.Empty
                        || roles.Length != 1 || roles[0].Value is not ("User" or "Administrator")
                        || ids.Length != 1 || !Guid.TryParse(ids[0].Value, out var tokenId) || tokenId == Guid.Empty)
                    { context.Fail("Invalid identity claims."); return; }
                    var metadata = context.HttpContext.GetEndpoint()?.Metadata;
                    if (metadata is null || metadata.OfType<IAllowAnonymous>().Any() || !metadata.OfType<IAuthorizeData>().Any()) return;
                    AuthorizationState? current = null;
                    try { current = await context.HttpContext.RequestServices.GetRequiredService<IAuthorizationStateReader>().FindAsync(userId, context.HttpContext.RequestAborted); }
                    catch (Exception error) when (error is NpgsqlException or TimeoutException
                        or InvalidOperationException { InnerException: NpgsqlException or TimeoutException })
                    { context.HttpContext.Items[StateUnavailable] = true; context.Fail("Identity state is unavailable."); return; }
                    var decision = CatalogAuthorization.Decide(new(subjects[0].Value, roles[0].Value, ids[0].Value), current, true);
                    if (decision.StatusCode == 503) { context.HttpContext.Items[StateUnavailable] = true; context.Fail("Identity state is unavailable."); }
                    else if (decision.StatusCode != 200) context.Fail("Account is unavailable.");
                    else context.HttpContext.Items[CurrentRole] = current!.Role;
                },
                OnChallenge = async context =>
                {
                    if (!context.HttpContext.Items.ContainsKey(StateUnavailable)) return;
                    context.HandleResponse(); context.Response.StatusCode = 503; context.Response.Headers.RetryAfter = "1";
                    await context.HttpContext.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new()
                    { HttpContext = context.HttpContext, ProblemDetails = ApiErrors.AuthStateUnavailable.ToProblemDetails(context.Request.Path) });
                }
            };
        });
        services.AddAuthorization(authorization =>
        {
            authorization.AddPolicy(IdentityAuthorizationPolicy.AdminOnly, policy => policy.RequireAuthenticatedUser().RequireRole("Administrator")
                .RequireAssertion(context => context.Resource is HttpContext http && http.Items.TryGetValue(CurrentRole, out var role) && role is IdentityRole.Administrator));
            authorization.AddPolicy(IdentityAuthorizationPolicy.UserOrAdmin, policy => policy.RequireAuthenticatedUser().RequireRole("User", "Administrator"));
        });
        return services;
    }
    private static bool HasUniqueMembers(string encodedJson)
    {
        try
        {
            using var json = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(encodedJson));
            if (json.RootElement.ValueKind != JsonValueKind.Object) return false;
            var names = new HashSet<string>(StringComparer.Ordinal);
            return json.RootElement.EnumerateObject().All(property => names.Add(property.Name));
        }
        catch (Exception error) when (error is JsonException or FormatException or ArgumentException) { return false; }
    }
}
