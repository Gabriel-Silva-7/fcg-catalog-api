using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace FCG.Catalog.Api.Tests.Catalog;

public sealed class CatalogPostgres : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder()
        .WithImage("postgres:16@sha256:f1c3376c26f2609ab9f29f71f824103fe2fcd8ee0346485cb6122a4f93df6f94")
        .WithDatabase("postgres").WithUsername("postgres").WithPassword("catalog-fixture-only")
        .Build();
    public string ConnectionString => container.GetConnectionString();
    public Task InitializeAsync() => container.StartAsync();
    public Task PauseAsync() => container.PauseAsync();
    public Task UnpauseAsync() => container.UnpauseAsync();
    public Task DisposeAsync() => container.DisposeAsync().AsTask();
}

public sealed class CatalogTestApplication : WebApplicationFactory<Program>, IAsyncDisposable
{
    private readonly CatalogPostgres postgres;
    private readonly RSA key = RSA.Create(2048);
    private readonly string directory = Path.Combine(Path.GetTempPath(), "fcg-cat01-" + Guid.NewGuid().ToString("N"));
    private readonly string database = "cat01_" + Guid.NewGuid().ToString("N");
    private readonly string role = "fcg_catalog_" + Guid.NewGuid().ToString("N");
    private readonly Action<IWebHostBuilder>? customize;
    public CapturedLogs Logs { get; } = new();
    public string ConnectionString { get; }
    public HttpClient Client { get; private set; } = null!;
    public Guid AdministratorId { get; } = Guid.NewGuid();
    private CatalogTestApplication(CatalogPostgres postgres, Action<IWebHostBuilder>? customize)
    {
        this.postgres = postgres;
        this.customize = customize;
        ConnectionString = new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { Database = database, Username = role, Password = "catalog-app-fixture", Timeout = 2, CommandTimeout = 3 }.ConnectionString;
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "public.pem"), key.ExportSubjectPublicKeyInfoPem());
    }
    public static async Task<CatalogTestApplication> StartAsync(CatalogPostgres postgres, Action<IWebHostBuilder>? customize = null)
    {
        var app = new CatalogTestApplication(postgres, customize);
        try
        {
            await using var connection = new NpgsqlConnection(postgres.ConnectionString);
            await connection.OpenAsync();
            await using var createRole = new NpgsqlCommand($"CREATE ROLE \"{app.role}\" LOGIN PASSWORD 'catalog-app-fixture' NOSUPERUSER NOCREATEDB NOCREATEROLE", connection);
            await createRole.ExecuteNonQueryAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{app.database}\" OWNER \"{app.role}\"", connection);
            await command.ExecuteNonQueryAsync();
            await using var restrict = new NpgsqlCommand($"REVOKE ALL ON DATABASE \"{app.database}\" FROM PUBLIC", connection);
            await restrict.ExecuteNonQueryAsync();
            app.Client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            return app;
        }
        catch { await app.DisposeAsync(); throw; }
    }
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production").ConfigureLogging(logging => logging.AddProvider(Logs));
        foreach (var (name, value) in new Dictionary<string, string>
        {
            ["ConnectionStrings:Default"] = ConnectionString,
            ["Jwt:Issuer"] = "fcg-fixture", ["Jwt:Audience"] = "fcg-catalog-fixture",
            ["Jwt:PublicKeyPath"] = Path.Combine(directory, "public.pem"),
            ["Database:MigrateOnStartup"] = "true", ["Swagger:Enabled"] = "true"
        }) builder.UseSetting(name, value);
        customize?.Invoke(builder);
    }
    public string Token(string role = "Administrator", string? subject = null, string? jwtId = null,
        string issuer = "fcg-fixture", string audience = "fcg-catalog-fixture", int expirySeconds = 900,
        bool wrongKey = false, string algorithm = "RS256", bool omitSubject = false, bool omitJwtId = false,
        int notBeforeSeconds = -120, bool duplicateSubject = false)
    {
        var payload = new Dictionary<string, object> {
            ["iss"] = issuer, ["aud"] = audience, ["role"] = role,
            ["iat"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["nbf"] = DateTimeOffset.UtcNow.AddSeconds(notBeforeSeconds).ToUnixTimeSeconds(),
            ["exp"] = DateTimeOffset.UtcNow.AddSeconds(expirySeconds).ToUnixTimeSeconds() };
        if (!omitSubject) payload["sub"] = subject ?? AdministratorId.ToString();
        if (!omitJwtId) payload["jti"] = jwtId ?? Guid.NewGuid().ToString();
        var json = JsonSerializer.Serialize(payload);
        if (duplicateSubject) json = json[..^1] + ",\"sub\":\"" + Guid.NewGuid() + "\"}";
        var body = Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = algorithm, typ = "JWT" })) + "." + Encode(Encoding.UTF8.GetBytes(json));
        using var other = wrongKey ? RSA.Create(2048) : null;
        var signature = (other ?? key).SignData(Encoding.ASCII.GetBytes(body), algorithm == "RS512" ? HashAlgorithmName.SHA512 : HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return body + "." + Encode(signature);
    }
    public void Authorize(string? token = null) => Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token ?? Token());
    public async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }
    public async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
    public override async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        await base.DisposeAsync();
        key.Dispose();
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)", connection);
        await command.ExecuteNonQueryAsync();
        await using var dropRole = new NpgsqlCommand($"DROP ROLE IF EXISTS \"{role}\"", connection);
        await dropRole.ExecuteNonQueryAsync();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
    private static string Encode(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
