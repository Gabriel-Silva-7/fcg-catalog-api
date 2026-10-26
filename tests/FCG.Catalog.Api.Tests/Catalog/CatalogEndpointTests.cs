using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;

namespace FCG.Catalog.Api.Tests.Catalog;

public sealed class CatalogEndpointTests(CatalogPostgres postgres) : IClassFixture<CatalogPostgres>
{
    private const string Endpoint = "/api/v1/games";

    [Fact]
    public async Task Admin_creates_normalized_game_in_own_database_and_public_location_is_readable()
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres);
        app.Authorize();
        using var response = await app.Client.PostAsJsonAsync(Endpoint, new { title = "  Celeste  ", description = "  Platformer  ", basePrice = 59.90m });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var game = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Celeste", game.GetProperty("title").GetString());
        Assert.Equal("Platformer", game.GetProperty("description").GetString());
        Assert.Equal(59.90m, game.GetProperty("basePrice").GetDecimal());
        Assert.Equal(59.90m, game.GetProperty("currentPrice").GetDecimal());
        Assert.Equal(0m, game.GetProperty("discountPercentage").GetDecimal());
        Assert.True(game.GetProperty("isActive").GetBoolean());
        Assert.False(game.TryGetProperty("createdByUserId", out _));
        var id = game.GetProperty("id").GetGuid();
        Assert.NotEqual(Guid.Empty, id);
        Assert.Equal(app.AdministratorId, await app.ScalarAsync<Guid>($"SELECT \"CreatedByUserId\" FROM \"Games\" WHERE \"Id\"='{id}'"));
        Assert.Equal(0L, await app.ScalarAsync<long>("SELECT count(*) FROM information_schema.table_constraints WHERE table_name='Games' AND constraint_type='FOREIGN KEY'"));
        app.Client.DefaultRequestHeaders.Authorization = null;
        using var detail = await app.Client.GetAsync(Assert.IsType<Uri>(response.Headers.Location));
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        Assert.Equal(id, (await detail.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Authentication_and_role_are_enforced_without_mutations()
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres);
        var invalid = new[] { "broken", app.Token(expirySeconds: -1), app.Token(issuer: "other"), app.Token(audience: "other"),
            app.Token(wrongKey: true), app.Token(algorithm: "RS512"), app.Token(notBeforeSeconds: 120), app.Token(subject: "bad"), app.Token(subject: Guid.Empty.ToString()),
            app.Token(jwtId: "bad"), app.Token(jwtId: Guid.Empty.ToString()), app.Token(omitSubject: true), app.Token(omitJwtId: true), app.Token(role: "admin") };
        using var anonymous = await app.Client.PostAsJsonAsync(Endpoint, new { title = "No", basePrice = 1m });
        await Problem(anonymous, HttpStatusCode.Unauthorized, "unauthenticated");
        foreach (var token in invalid)
        {
            app.Authorize(token);
            using var response = await app.Client.PostAsJsonAsync(Endpoint, new { title = "No", basePrice = 1m });
            await Problem(response, HttpStatusCode.Unauthorized, "unauthenticated");
        }
        app.Authorize(app.Token(role: "User"));
        using var forbidden = await app.Client.PostAsJsonAsync(Endpoint, new { title = "No", basePrice = 1m });
        await Problem(forbidden, HttpStatusCode.Forbidden, "forbidden");
        Assert.Equal(0L, await app.ScalarAsync<long>("SELECT count(*) FROM \"Games\""));
    }

    [Fact]
    public async Task Duplicate_subject_in_signed_payload_is_rejected_without_creating_game()
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres);
        app.Authorize(app.Token(duplicateSubject: true));
        using var response = await app.Client.PostAsJsonAsync(Endpoint, new { title = "Ambiguous identity", basePrice = 1m });
        await Problem(response, HttpStatusCode.Unauthorized, "unauthenticated");
        Assert.Equal(0L, await app.ScalarAsync<long>("SELECT count(*) FROM \"Games\""));
    }

    [Fact]
    public async Task Public_catalog_filters_inactive_games_and_preserves_literal_search()
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres);
        var titles = new[] { "100% Fun", "1000 Fun", "Under_score", "UnderXscore", @"Back\slash", "BackXslash", "Celeste", "Hidden" };
        foreach (var title in titles) await Create(app, title, 10m);
        await app.ExecuteAsync("UPDATE \"Games\" SET \"IsActive\"=false WHERE \"Title\"='Hidden'");
        var hiddenId = await app.ScalarAsync<Guid>("SELECT \"Id\" FROM \"Games\" WHERE \"Title\"='Hidden'");
        app.Authorize("invalid-but-public");
        foreach (var pair in new[] { ("100%", "100% Fun"), ("Under_", "Under_score"), (@"Back\", @"Back\slash"), (" CELESTE ", "Celeste") })
        {
            using var response = await app.Client.GetAsync(Endpoint + "?search=" + Uri.EscapeDataString(pair.Item1));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(new[] { pair.Item2 }, Titles(body));
            Assert.Equal(1, body.GetProperty("totalCount").GetInt32());
        }
        using var all = await app.Client.GetAsync(Endpoint);
        var page = await all.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, page.GetProperty("page").GetInt32()); Assert.Equal(20, page.GetProperty("pageSize").GetInt32());
        Assert.Equal(7, page.GetProperty("totalCount").GetInt32()); Assert.DoesNotContain("Hidden", Titles(page));
        foreach (var id in new[] { hiddenId, Guid.NewGuid() })
        {
            using var response = await app.Client.GetAsync(Endpoint + "/" + id);
            await Problem(response, HttpStatusCode.NotFound, "resource_not_found");
        }
    }

    [Fact]
    public async Task Pagination_is_stable_with_ties_and_allowlisted_sorting()
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres);
        foreach (var game in new[] { ("Third", 20m), ("First", 30m), ("Second", 10m) }) await Create(app, game.Item1, game.Item2);
        await app.ExecuteAsync("UPDATE \"Games\" SET \"CreatedAtUtc\"=CASE \"Title\" WHEN 'First' THEN '2020-01-01'::timestamptz WHEN 'Second' THEN '2020-01-02'::timestamptz ELSE '2020-01-03'::timestamptz END");
        foreach (var sample in new[] { ("title", new[] { "First", "Second", "Third" }), ("basePrice", new[] { "Second", "Third", "First" }), ("createdAt", new[] { "First", "Second", "Third" }) })
        {
            using var response = await app.Client.GetAsync(Endpoint + "?sortBy=" + sample.Item1);
            Assert.Equal(sample.Item2, Titles(await response.Content.ReadFromJsonAsync<JsonElement>()));
        }
        await app.ExecuteAsync("UPDATE \"Games\" SET \"Title\"='Same', \"BasePrice\"=1, \"CreatedAtUtc\"='2020-01-01'");
        foreach (var sort in new[] { "title", "basePrice", "createdAt" })
        {
            var ids = new List<Guid>();
            for (var number = 1; number <= 3; number++)
            {
                using var response = await app.Client.GetAsync($"{Endpoint}?page={number}&pageSize=1&sortBy={sort}");
                var body = await response.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal(3, body.GetProperty("totalCount").GetInt32());
                ids.Add(body.GetProperty("items")[0].GetProperty("id").GetGuid());
            }
            Assert.Equal(3, ids.Distinct().Count()); Assert.Equal(ids.Order(), ids);
        }
        using var beyond = await app.Client.GetAsync(Endpoint + "?page=1000000");
        Assert.Empty((await beyond.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Invalid_queries_and_creation_inputs_return_canonical_errors_without_partial_writes()
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres);
        foreach (var query in new[] { "page=0", "page=1000001", "page=x", "pageSize=0", "pageSize=101", "sortBy=description", "search=" + new string('a', 201) })
        {
            using var response = await app.Client.GetAsync(Endpoint + "?" + query);
            await Problem(response, HttpStatusCode.BadRequest, "validation_error");
        }
        app.Authorize();
        var invalidBodies = new[] { "{}", "{\"title\":\"Game\"}", "{\"title\":\"Game\",\"basePrice\":null}", "{\"title\":\"   \",\"basePrice\":1}",
            JsonSerializer.Serialize(new { title = new string('a', 201), basePrice = 1m }), JsonSerializer.Serialize(new { title = "Game", description = new string('a', 2001), basePrice = 1m }),
            "{\"title\":\"Game\",\"basePrice\":-0.01}", "{\"title\":\"Game\",\"basePrice\":1.001}", "{\"title\":\"Game\",\"basePrice\":10000000000000000}",
            "{\"title\":\"Game\",\"basePrice\":1.000000000000000000000000000000000001}" };
        foreach (var body in invalidBodies)
        {
            using var response = await app.Client.PostAsync(Endpoint, new StringContent(body, Encoding.UTF8, "application/json"));
            await Problem(response, HttpStatusCode.BadRequest, "validation_error");
        }
        Assert.Equal(0L, await app.ScalarAsync<long>("SELECT count(*) FROM \"Games\""));
        await Create(app, new string('a', 200), 9999999999999999.99m);
        await Create(app, "Free", 0m); await Create(app, "Free", 0m);
        Assert.Equal(3L, await app.ScalarAsync<long>("SELECT count(*) FROM \"Games\""));
    }

    [Fact]
    public async Task Openapi_describes_public_reads_and_authenticated_creation()
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres);
        using var response = await app.Client.GetAsync("/swagger/v1/swagger.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var paths = body.GetProperty("paths");
        var list = paths.GetProperty(Endpoint).GetProperty("get");
        var post = paths.GetProperty(Endpoint).GetProperty("post");
        var detail = paths.GetProperty(Endpoint + "/{id}").GetProperty("get");
        Assert.False(list.TryGetProperty("security", out var listSecurity) && listSecurity.GetArrayLength() > 0);
        Assert.False(detail.TryGetProperty("security", out var detailSecurity) && detailSecurity.GetArrayLength() > 0);
        var security = post.GetProperty("security");
        Assert.Equal(1, security.GetArrayLength());
        Assert.Equal(0, security[0].GetProperty("Bearer").GetArrayLength());
        Assert.Equal(new[] { "page", "pageSize", "search", "sortBy" }, list.GetProperty("parameters").EnumerateArray().Select(p => p.GetProperty("name").GetString()).Order());
        var schemas = body.GetProperty("components").GetProperty("schemas");
        var createSchema = schemas.GetProperty("CreateGameRequest");
        Assert.Contains("basePrice", createSchema.GetProperty("required").EnumerateArray().Select(p => p.GetString()));
        var price = createSchema.GetProperty("properties").GetProperty("basePrice");
        Assert.Equal("number", price.GetProperty("type").GetString());
        Assert.Equal(0m, price.GetProperty("minimum").GetDecimal());
        Assert.Equal(9999999999999999.99m, price.GetProperty("maximum").GetDecimal());
        foreach (var code in new[] { "400", "401", "403" })
            Assert.True(post.GetProperty("responses").GetProperty(code).TryGetProperty("content", out _));
        Assert.Equal("bearer", body.GetProperty("components").GetProperty("securitySchemes").GetProperty("Bearer").GetProperty("scheme").GetString());
    }

    private static async Task Create(CatalogTestApplication app, string title, decimal price)
    {
        app.Authorize();
        using var response = await app.Client.PostAsJsonAsync(Endpoint, new { title, basePrice = price });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }
    private static string[] Titles(JsonElement page) => page.GetProperty("items").EnumerateArray().Select(game => game.GetProperty("title").GetString()!).ToArray();
    private static async Task Problem(HttpResponseMessage response, HttpStatusCode expected, string code)
    {
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, body.GetProperty("code").GetString()); Assert.Equal((int)expected, body.GetProperty("status").GetInt32());
        Assert.StartsWith("urn:fcg:error:", body.GetProperty("type").GetString());
        Assert.StartsWith(Endpoint, body.GetProperty("instance").GetString()); Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("traceId").GetString()));
    }
}
