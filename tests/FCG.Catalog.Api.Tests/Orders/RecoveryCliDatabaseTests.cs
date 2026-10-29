using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using FCG.Catalog.Api.Tests.Catalog;
using FCG.Catalog.Domain.Orders;
using FCG.Catalog.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using static FCG.Catalog.Api.Tests.Orders.PaymentApplicationTests;
using static FCG.Catalog.Api.Tests.Orders.RecoveryCliTests;

namespace FCG.Catalog.Api.Tests.Orders;

public sealed class RecoveryCliDatabaseTests(CatalogPostgres postgres) : IClassFixture<CatalogPostgres>
{
    [Fact]
    public async Task List_show_dry_run_apply_and_repeat_use_original_conflict_without_recreating_order()
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres); var (order, id) = await MissingOrder(app);
        var payload = await app.ScalarAsync<string>("SELECT \"Payload\" FROM \"MessageConflicts\"");
        var before = await Fingerprint(app);
        var list = await Run(app.ConnectionString, "recover-payments", "list", "--limit", "1"); Assert.Equal(0, list.ExitCode);
        var item = JsonDocument.Parse(list.Output).RootElement.GetProperty("items")[0];
        Assert.Equal(id, item.GetProperty("id").GetGuid()); Assert.False(item.TryGetProperty("payload", out _));
        Assert.False(item.TryGetProperty("email", out _)); Assert.Equal("OrderNotFound", item.GetProperty("reason").GetString());
        Assert.Equal(0, (await Run(app.ConnectionString, "recover-payments", "show", "--id", id.ToString())).ExitCode);
        var preview = await Run(app.ConnectionString, "recover-payments", "replay", "--id", id.ToString());
        Assert.Equal(4, preview.ExitCode); Assert.True(JsonDocument.Parse(preview.Output).RootElement.GetProperty("dryRun").GetBoolean());
        Assert.Equal(before, await Fingerprint(app));
        Assert.Equal(4, (await Run(app.ConnectionString, "recover-payments", "replay", "--id", id.ToString(), "--apply")).ExitCode);
        Assert.Equal(0L, await app.ScalarAsync<long>("SELECT count(*) FROM \"Orders\""));
        Assert.Equal(1L, await app.ScalarAsync<long>("SELECT count(*) FROM \"PaymentRecoveryAttempts\""));
        await Restore(app, order);
        before = await Fingerprint(app);
        preview = await Run(app.ConnectionString, "recover-payments", "replay", "--id", id.ToString(), "--dry-run"); Assert.Equal(0, preview.ExitCode);
        Assert.Equal("Approved", JsonDocument.Parse(preview.Output).RootElement.GetProperty("outcome").GetString());
        Assert.Equal(before, await Fingerprint(app));
        var apply = await Run(app.ConnectionString, "recover-payments", "replay", "--id", id.ToString(), "--apply"); Assert.Equal(0, apply.ExitCode);
        Assert.Equal("Approved", JsonDocument.Parse(apply.Output).RootElement.GetProperty("outcome").GetString());
        var repeated = await Run(app.ConnectionString, "recover-payments", "replay", "--id", id.ToString(), "--apply"); Assert.Equal(0, repeated.ExitCode);
        Assert.Equal("AlreadyResolved", JsonDocument.Parse(repeated.Output).RootElement.GetProperty("outcome").GetString());
        Assert.Equal(1L, await app.ScalarAsync<long>("SELECT count(*) FROM \"LibraryEntries\""));
        Assert.Equal(3L, await app.ScalarAsync<long>("SELECT count(*) FROM \"PaymentRecoveryAttempts\""));
        Assert.Equal(payload, await app.ScalarAsync<string>("SELECT \"Payload\" FROM \"MessageConflicts\""));
        Assert.DoesNotContain(app.ConnectionString, list.Output + list.Error + apply.Output + apply.Error);
    }

    [Fact]
    public async Task Concurrent_operator_processes_apply_one_effect_and_record_two_attempts()
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres); var (order, id) = await MissingOrder(app); await Restore(app, order);
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Run(app.ConnectionString, "recover-payments", "replay", "--id", id.ToString(), "--apply")));
        Assert.All(results, result => Assert.Equal(0, result.ExitCode));
        Assert.Equal(new[] { "AlreadyResolved", "Approved" }, results.Select(r => JsonDocument.Parse(r.Output).RootElement.GetProperty("outcome").GetString()).Order());
        Assert.Equal(1L, await app.ScalarAsync<long>("SELECT count(*) FROM \"LibraryEntries\""));
        Assert.Equal(2L, await app.ScalarAsync<long>("SELECT count(*) FROM \"PaymentRecoveryAttempts\""));
    }

    [Fact]
    public async Task Operator_never_starts_http_or_requires_keys_broker_or_runs_migrations()
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres);
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var result = await RunConfigured(app.ConnectionString, new Dictionary<string, string?> { ["ASPNETCORE_URLS"] = "http://127.0.0.1:" + port }, "recover-payments", "list");
        Assert.Equal(0, result.ExitCode); Assert.Empty(result.Error);
        Assert.Empty(JsonDocument.Parse(result.Output).RootElement.GetProperty("items").EnumerateArray());
        await app.ExecuteAsync("DROP SCHEMA public CASCADE; CREATE SCHEMA public");
        result = await Run(app.ConnectionString, "recover-payments", "list"); Assert.Equal(1, result.ExitCode);
        Assert.Equal(0L, await app.ScalarAsync<long>("SELECT count(*) FROM information_schema.tables WHERE table_schema='public'"));
        Assert.DoesNotContain("   at ", result.Error); Assert.DoesNotContain(app.ConnectionString, result.Error);
    }

    [Fact]
    public async Task Missing_selection_and_connection_failure_have_distinct_safe_exit_codes()
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres); var missing = Guid.NewGuid().ToString();
        Assert.Equal(3, (await Run(app.ConnectionString, "recover-payments", "show", "--id", missing)).ExitCode);
        Assert.Equal(3, (await Run(app.ConnectionString, "recover-payments", "replay", "--id", missing)).ExitCode);
        var connection = new NpgsqlConnectionStringBuilder(app.ConnectionString) { Host = "127.0.0.1", Port = 1, Password = "PRIVATE-CONNECTION-PASSWORD" }.ConnectionString;
        var failure = await Run(connection, "recover-payments", "list"); Assert.Equal(1, failure.ExitCode);
        Assert.DoesNotContain("PRIVATE-CONNECTION-PASSWORD", failure.Output + failure.Error); Assert.DoesNotContain("   at ", failure.Error);
    }

    [Theory]
    [InlineData("type", "UnsupportedMessageType")]
    [InlineData("hash", "InvalidStoredPayload")]
    [InlineData("terminal", "DivergentPaymentResult")]
    public async Task Preview_and_apply_preserve_incompatible_or_tampered_state(string change, string expected)
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres); var (order, id) = await MissingOrder(app); await Restore(app, order);
        if (change == "type") await app.ExecuteAsync("UPDATE \"MessageConflicts\" SET \"MessageType\"='UserCreatedEvent',\"Kind\"='Identity'");
        else if (change == "hash") await app.ExecuteAsync("UPDATE \"MessageConflicts\" SET \"PayloadHash\"='tampered'");
        else await Apply(app, Payment(order, "Rejected"));
        var before = await Fingerprint(app);
        var preview = await Run(app.ConnectionString, "recover-payments", "replay", "--id", id.ToString()); Assert.Equal(4, preview.ExitCode);
        Assert.Equal(expected, JsonDocument.Parse(preview.Output).RootElement.GetProperty("reason").GetString());
        Assert.Equal(before, await Fingerprint(app));
        var apply = await Run(app.ConnectionString, "recover-payments", "replay", "--id", id.ToString(), "--apply"); Assert.Equal(4, apply.ExitCode);
        Assert.Equal(expected, JsonDocument.Parse(apply.Output).RootElement.GetProperty("reason").GetString());
        Assert.False(await app.ScalarAsync<bool>("SELECT \"Resolved\" FROM \"MessageConflicts\""));
        Assert.Equal(0L, await app.ScalarAsync<long>("SELECT count(*) FROM \"LibraryEntries\""));
    }

    private static async Task<(Order Order, Guid Conflict)> MissingOrder(CatalogTestApplication app)
    {
        var order = await Setup(app);
        // Deliberate owner-side partial-restore fixture; never part of broker persistence proof.
        await app.ExecuteAsync($"DELETE FROM \"Orders\" WHERE \"Id\"='{order.Id}'");
        await Apply(app, Payment(order));
        return (order, await app.ScalarAsync<Guid>("SELECT \"Id\" FROM \"MessageConflicts\""));
    }
    private static async Task Restore(CatalogTestApplication app, Order order)
    {
        await using var scope = app.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"Orders\" (\"Id\",\"UserId\",\"GameId\",\"Price\",\"Status\",\"PlacedAtUtc\",\"Version\") VALUES ({order.Id},{order.UserId},{order.GameId},{order.Price},'Pending',{order.PlacedAtUtc},1)");
    }
    private static async Task<string> Fingerprint(CatalogTestApplication app)
    {
        var values = new List<string>();
        foreach (var table in new[] { "Orders", "LibraryEntries", "MessageConflicts", "PaymentRecoveryAttempts" })
            values.Add(await app.ScalarAsync<string>($"SELECT coalesce(json_agg(t)::text,'[]') FROM (SELECT *,xmin::text row_token FROM \"{table}\" ORDER BY ctid)t"));
        return string.Join('|', values);
    }
}
