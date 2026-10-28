using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace FCG.Catalog.Api.Tests.Catalog;

public sealed class JwtConfigurationTests
{
    [Theory]
    [InlineData("Jwt:Issuer", "")]
    [InlineData("Jwt:Audience", "")]
    [InlineData("Jwt:Algorithm", "HS256")]
    [InlineData("Jwt:ExpirationMinutes", "30")]
    [InlineData("Jwt:ClockSkewSeconds", "60")]
    [InlineData("Jwt:PrivateKeyPath", "forbidden-private-key.pem")]
    [InlineData("ConnectionStrings:Default", "")]
    public async Task Host_rejects_missing_or_incompatible_configuration(string name, string value)
    {
        await using var app = new ConfigurationApplication(name, value);
        Assert.ThrowsAny<Exception>(() => app.CreateClient());
    }

    [Theory]
    [InlineData("private")]
    [InlineData("pkcs1")]
    [InlineData("weak")]
    [InlineData("malformed")]
    public async Task Host_rejects_private_non_spki_weak_or_malformed_public_key(string kind)
    {
        await using var app = new ConfigurationApplication(keyKind: kind);
        var exception = Assert.ThrowsAny<Exception>(() => app.CreateClient());
        Assert.DoesNotContain("PRIVATE-KEY-SENTINEL", exception.ToString());
    }

    [Fact]
    public async Task Public_key_only_host_starts_without_mutating_database_and_swagger_is_opt_in()
    {
        await using var app = new ConfigurationApplication();
        using var client = app.CreateClient();
        using var live = await client.GetAsync("/health/live");
        using var ready = await client.GetAsync("/health/ready");
        using var docs = await client.GetAsync("/swagger/v1/swagger.json");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, docs.StatusCode);
    }

    private sealed class ConfigurationApplication : WebApplicationFactory<Program>
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "fcg-catalog-config-" + Guid.NewGuid().ToString("N"));
        private readonly string? name;
        private readonly string? value;
        public ConfigurationApplication(string? name = null, string? value = null, string keyKind = "public")
        {
            this.name = name; this.value = value;
            Directory.CreateDirectory(directory);
            using var rsa = RSA.Create(keyKind == "weak" ? 1024 : 2048);
            var pem = keyKind switch
            {
                "private" => rsa.ExportPkcs8PrivateKeyPem(),
                "pkcs1" => rsa.ExportRSAPublicKeyPem(),
                "malformed" => "PRIVATE-KEY-SENTINEL",
                _ => rsa.ExportSubjectPublicKeyInfoPem()
            };
            File.WriteAllText(Path.Combine(directory, "key.pem"), pem);
        }
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("ConnectionStrings:Default", "Host=127.0.0.1;Port=1;Database=not-accessible;Username=catalog;Password=test-only;Timeout=1;Command Timeout=1");
            builder.UseSetting("RabbitMq:Host", "127.0.0.1");
            builder.UseSetting("RabbitMq:Port", "1");
            builder.UseSetting("RabbitMq:VirtualHost", "/fcg");
            builder.UseSetting("RabbitMq:Username", "fcg_catalog");
            builder.UseSetting("RabbitMq:Password", "test-only");
            builder.UseSetting("Jwt:Issuer", "fcg-fixture");
            builder.UseSetting("Jwt:Audience", "fcg-fixture");
            builder.UseSetting("Jwt:PublicKeyPath", Path.Combine(directory, "key.pem"));
            if (name is not null) builder.UseSetting(name, value!);
        }
        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
