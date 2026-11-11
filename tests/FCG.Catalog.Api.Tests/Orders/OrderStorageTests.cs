using FCG.Catalog.Api.Tests.Catalog;
using Xunit;
using FCG.Catalog.Application.Orders;
using FCG.Catalog.Domain.Orders;
using FCG.Catalog.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FCG.Catalog.Api.Tests.Orders;

public sealed class OrderStorageTests(CatalogPostgres postgres) : IClassFixture<CatalogPostgres>
{
    [Fact]
    public async Task Application_migrations_provision_order_storage()
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres);
        Assert.True(await app.ScalarAsync<bool>("SELECT to_regclass('\"Orders\"') IS NOT NULL"));
    }
    [Fact]
    public async Task Unique_partial_index_rejects_concurrent_open_orders_for_same_pair()
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres); var user = Guid.NewGuid(); var game = Guid.NewGuid();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<bool> Insert()
        {
            using var scope = app.Services.CreateScope(); var repository = scope.ServiceProvider.GetRequiredService<IOrderRepository>();
            repository.Add(Order.Place(user, game, 44.93m, Instant)); await gate.Task;
            try { await repository.SaveChangesAsync(CancellationToken.None); return true; }
            catch (DbUpdateException error) when (error.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "UX_Orders_OpenUserGame" })
            { return false; }
        }
        var first = Insert(); var second = Insert(); gate.SetResult(); var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Single(results, value => value); Assert.Single(results, value => !value);
        Assert.Equal(1L, await app.ScalarAsync<long>("SELECT count(*) FROM \"Orders\""));
    }

    [Fact]
    public async Task Rejected_releases_pair_but_approved_keeps_it_reserved_across_contexts()
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres); var user = Guid.NewGuid(); var game = Guid.NewGuid();
        var first = Order.Place(user, game, 600m, Instant); first.Complete(OrderStatus.Rejected, "AmountAboveLimit", Instant.AddSeconds(2));
        using (var scope = app.Services.CreateScope())
        { var repository = scope.ServiceProvider.GetRequiredService<IOrderRepository>(); repository.Add(first); await repository.SaveChangesAsync(CancellationToken.None); }
        var accepted = Order.Place(user, game, 400m, Instant.AddSeconds(4)); accepted.Complete(OrderStatus.Approved, null, Instant.AddSeconds(6));
        using (var scope = app.Services.CreateScope())
        { var repository = scope.ServiceProvider.GetRequiredService<IOrderRepository>(); repository.Add(accepted); await repository.SaveChangesAsync(CancellationToken.None); }
        using var contender = app.Services.CreateScope(); var blocked = contender.ServiceProvider.GetRequiredService<IOrderRepository>();
        blocked.Add(Order.Place(user, game, 500m, Instant.AddSeconds(8)));
        var collision = await Assert.ThrowsAsync<DbUpdateException>(() => blocked.SaveChangesAsync(CancellationToken.None));
        Assert.Equal("UX_Orders_OpenUserGame", Assert.IsType<PostgresException>(collision.InnerException).ConstraintName);
        Assert.Equal(2L, await app.ScalarAsync<long>("SELECT count(*) FROM \"Orders\""));
    }

    [Fact]
    public async Task Optimistic_version_rejects_stale_terminal_write_and_snapshot_survives_reload()
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres); var placed = Order.Place(Guid.NewGuid(), Guid.NewGuid(), 44.93m, Instant);
        using (var setup = app.Services.CreateScope())
        { var repository = setup.ServiceProvider.GetRequiredService<IOrderRepository>(); repository.Add(placed); await repository.SaveChangesAsync(CancellationToken.None); }
        using var one = app.Services.CreateScope(); using var two = app.Services.CreateScope();
        var firstRepo = one.ServiceProvider.GetRequiredService<IOrderRepository>(); var secondRepo = two.ServiceProvider.GetRequiredService<IOrderRepository>();
        var first = (await firstRepo.FindAsync(placed.Id, CancellationToken.None))!; var second = (await secondRepo.FindAsync(placed.Id, CancellationToken.None))!;
        first.Complete(OrderStatus.Approved, null, Instant.AddSeconds(2)); await firstRepo.SaveChangesAsync(CancellationToken.None);
        second.Complete(OrderStatus.Rejected, "AmountAboveLimit", Instant.AddSeconds(3));
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => secondRepo.SaveChangesAsync(CancellationToken.None));
        using var read = app.Services.CreateScope(); var db = read.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var saved = await db.Orders.AsNoTracking().SingleAsync();
        Assert.Equal(OrderStatus.Approved, saved.Status); Assert.Equal(2, saved.Version); Assert.Equal(44.93m, saved.Price);
        Assert.Equal(Instant, saved.PlacedAtUtc); Assert.Equal(Instant.AddSeconds(2), saved.CompletedAtUtc);
    }

    [Theory]
    [InlineData("\"Price\" = -0.01", "CK_Orders_Price")]
    [InlineData("\"UserId\" = '00000000-0000-0000-0000-000000000000'::uuid", "CK_Orders_Identities")]
    [InlineData("\"Status\" = 'Unknown'", "CK_Orders_Result")]
    [InlineData("\"Status\" = 'Rejected', \"CompletedAtUtc\" = now(), \"Version\" = 2", "CK_Orders_Result")]
    public async Task Database_constraints_reject_invalid_state_even_without_domain_code(string update, string constraint)
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres); using var scope = app.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IOrderRepository>();
        var order = Order.Place(Guid.NewGuid(), Guid.NewGuid(), 1m, Instant); repository.Add(order); await repository.SaveChangesAsync(CancellationToken.None);
        var error = await Assert.ThrowsAsync<PostgresException>(() => app.ExecuteAsync($"UPDATE \"Orders\" SET {update} WHERE \"Id\" = '{order.Id}'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState); Assert.Equal(constraint, error.ConstraintName);
    }

    [Fact]
    public async Task Repository_participates_in_caller_transaction_and_readiness_checks_order_schema()
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>(); await using var transaction = await db.Database.BeginTransactionAsync();
            var repository = scope.ServiceProvider.GetRequiredService<IOrderRepository>();
            repository.Add(Order.Place(Guid.NewGuid(), Guid.NewGuid(), 1m, Instant)); await repository.SaveChangesAsync(CancellationToken.None);
            await transaction.RollbackAsync();
        }
        Assert.Equal(0L, await app.ScalarAsync<long>("SELECT count(*) FROM \"Orders\""));
        Assert.Equal("18,2", await app.ScalarAsync<string>("SELECT numeric_precision::text || ',' || numeric_scale::text FROM information_schema.columns WHERE table_name='Orders' AND column_name='Price'"));
        Assert.Equal(0L, await app.ScalarAsync<long>("SELECT count(*) FROM information_schema.table_constraints WHERE table_name='Orders' AND constraint_type='FOREIGN KEY'"));
        await app.ExecuteAsync("ALTER TABLE \"Orders\" RENAME COLUMN \"Price\" TO \"UnavailablePrice\"");
        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, (await app.Client.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.OK, (await app.Client.GetAsync("/health/live")).StatusCode);
        Assert.DoesNotContain("   at ", string.Join('\n', app.Logs.Entries));
    }
    [Fact]
    public async Task Recorded_order_price_is_not_recomputed_when_game_changes_or_is_deactivated()
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres);
        Guid orderId;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            var game = FCG.Catalog.Domain.Catalog.Game.Create("Snapshot", null, 59.90m, Guid.NewGuid(), Instant.UtcDateTime);
            db.Games.Add(game); var order = Order.Place(Guid.NewGuid(), game.Id, 44.93m, Instant); orderId = order.Id;
            db.Orders.Add(order); await db.SaveChangesAsync();
            game.Deactivate(); await db.SaveChangesAsync();
            await app.ExecuteAsync($"UPDATE \"Games\" SET \"BasePrice\" = 99 WHERE \"Id\" = '{game.Id}'");
        }
        using var read = app.Services.CreateScope();
        var saved = (await read.ServiceProvider.GetRequiredService<IOrderRepository>().FindAsync(orderId, CancellationToken.None))!;
        Assert.Equal(44.93m, saved.Price); Assert.Equal(OrderStatus.Pending, saved.Status); Assert.Equal(Instant, saved.PlacedAtUtc);
    }

    private static readonly DateTimeOffset Instant = DateTimeOffset.Parse("2026-10-28T15:00:00.123456Z");
}
