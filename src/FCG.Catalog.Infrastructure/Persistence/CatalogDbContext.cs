using FCG.Catalog.Domain.Catalog;
using FCG.Catalog.Domain.Orders;
using Microsoft.EntityFrameworkCore;
using MassTransit;

namespace FCG.Catalog.Infrastructure.Persistence;

public sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<Game> Games => Set<Game>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();
        var order = modelBuilder.Entity<Order>();
        order.ToTable("Orders", table =>
        {
            table.HasCheckConstraint("CK_Orders_Identities", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"UserId\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"GameId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
            table.HasCheckConstraint("CK_Orders_Price", "\"Price\" >= 0");
            table.HasCheckConstraint("CK_Orders_Result", "(\"Status\" = 'Pending' AND \"CompletedAtUtc\" IS NULL AND \"RejectionReason\" IS NULL AND \"Version\" = 1) OR (\"Status\" = 'Approved' AND \"CompletedAtUtc\" IS NOT NULL AND \"RejectionReason\" IS NULL AND \"Version\" = 2) OR (\"Status\" = 'Rejected' AND \"CompletedAtUtc\" IS NOT NULL AND \"RejectionReason\" IS NOT NULL AND \"RejectionReason\" = 'AmountAboveLimit' AND \"Version\" = 2)");
        });
        order.HasKey(x => x.Id);
        order.Property(x => x.Id).ValueGeneratedNever();
        order.Property(x => x.Price).HasPrecision(18, 2);
        order.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        order.Property(x => x.RejectionReason).HasMaxLength(40);
        order.Property(x => x.Version).IsConcurrencyToken();
        order.HasIndex(x => new { x.UserId, x.GameId }).IsUnique().HasDatabaseName("UX_Orders_OpenUserGame")
            .HasFilter("\"Status\" IN ('Pending', 'Approved')");
        order.HasIndex(x => new { x.UserId, x.PlacedAtUtc, x.Id });
        var game = modelBuilder.Entity<Game>();
        game.ToTable("Games", table => table.HasCheckConstraint("CK_Games_BasePrice_NonNegative", "\"BasePrice\" >= 0"));
        game.HasKey(x => x.Id).HasName("PK_Games");
        game.Property(x => x.Id).ValueGeneratedNever();
        game.Property(x => x.Title).HasMaxLength(Game.MaxTitleLength).IsRequired();
        game.Property(x => x.Description).HasMaxLength(Game.MaxDescriptionLength);
        game.Property(x => x.BasePrice).HasPrecision(18, Game.BasePriceScale);
        game.Property(x => x.CreatedAtUtc).HasColumnType("timestamp with time zone");
        // Audit identifier only. Catalog never queries or references a Users database.
        game.HasIndex(x => x.CreatedByUserId).HasDatabaseName("IX_Games_CreatedByUserId");
    }
}
