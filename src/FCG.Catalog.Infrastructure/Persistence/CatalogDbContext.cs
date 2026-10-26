using FCG.Catalog.Domain.Catalog;
using Microsoft.EntityFrameworkCore;

namespace FCG.Catalog.Infrastructure.Persistence;

public sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
{
    public DbSet<Game> Games => Set<Game>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
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
