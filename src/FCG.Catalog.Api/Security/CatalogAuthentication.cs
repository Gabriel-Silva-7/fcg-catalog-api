using System.Security.Cryptography;
using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using FCG.Contracts;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace FCG.Catalog.Api.Security;

public static class CatalogAuthentication
{
    public static IServiceCollection AddCatalogAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection("Jwt").Get<JwtContractOptions>() ?? new();
        options.Validate(isIssuer: false);
        var pem = File.ReadAllText(options.PublicKeyPath);
        var fields = PemEncoding.Find(pem);
        if (pem[fields.Label] != "PUBLIC KEY") throw new ArgumentException("Catalog requires a SubjectPublicKeyInfo PUBLIC KEY PEM.");
        using var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
        if (rsa.KeySize < 2048) throw new ArgumentException("JWT RSA public keys must contain at least 2048 bits.");
        var publicKey = new RsaSecurityKey(rsa.ExportParameters(false));
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(jwt =>
        {
            jwt.MapInboundClaims = false;
            jwt.IncludeErrorDetails = false;
            jwt.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true, IssuerSigningKey = publicKey,
                RequireSignedTokens = true, ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                ValidateIssuer = true, ValidIssuer = options.Issuer,
                ValidateAudience = true, ValidAudience = options.Audience,
                ValidateLifetime = true, RequireExpirationTime = true,
                ClockSkew = TimeSpan.Zero, NameClaimType = "sub", RoleClaimType = "role"
            };
            jwt.Events = new JwtBearerEvents
            {
                OnTokenValidated = context =>
                {
                    // Signature validation precedes this check. Inspect original JSON because
                    // the token reader can collapse duplicate members before creating claims.
                    var (header, payload) = context.SecurityToken switch
                    {
                        JsonWebToken token => (token.EncodedHeader, token.EncodedPayload),
                        JwtSecurityToken token => (token.RawHeader, token.RawPayload),
                        _ => ("", "")
                    };
                    if (!HasUniqueMembers(header) || !HasUniqueMembers(payload))
                    {
                        context.Fail("Ambiguous identity token.");
                        return Task.CompletedTask;
                    }
                    var principal = context.Principal!;
                    var subjects = principal.FindAll("sub").ToArray();
                    var roles = principal.FindAll("role").ToArray();
                    var ids = principal.FindAll("jti").ToArray();
                    if (subjects.Length != 1 || !Guid.TryParse(subjects[0].Value, out var userId) || userId == Guid.Empty
                        || roles.Length != 1 || (roles[0].Value != "User" && roles[0].Value != "Administrator")
                        || ids.Length != 1 || !Guid.TryParse(ids[0].Value, out var tokenId) || tokenId == Guid.Empty)
                        context.Fail("Invalid identity claims.");
                    return Task.CompletedTask;
                }
            };
        });
        services.AddAuthorization(authorization =>
        {
            authorization.AddPolicy(IdentityAuthorizationPolicy.AdminOnly, policy => policy.RequireAuthenticatedUser().RequireRole("Administrator"));
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
        catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException)
        {
            return false;
        }
    }

}
