using FCG.Catalog.Infrastructure.Identity;
using FCG.Catalog.Domain.Catalog;
using FCG.Catalog.Domain.Orders;
using FCG.Catalog.Domain.Library;
using Microsoft.EntityFrameworkCore;
using MassTransit;

namespace FCG.Catalog.Infrastructure.Persistence;

public sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
{
    public DbSet<UserAuthorizationState> UserAuthorizationStates => Set<UserAuthorizationState>();
    public DbSet<LibraryEntry> LibraryEntries => Set<LibraryEntry>();
    public DbSet<MessageConflict> MessageConflicts => Set<MessageConflict>();
    public DbSet<PaymentRecoveryAttempt> PaymentRecoveryAttempts => Set<PaymentRecoveryAttempt>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<Game> Games => Set<Game>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var identity = modelBuilder.Entity<UserAuthorizationState>();
        identity.ToTable("UserAuthorizationStates", table =>
        {
            table.HasCheckConstraint("CK_UserAuthorizationStates_Identity", "\"UserId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
            table.HasCheckConstraint("CK_UserAuthorizationStates_Version", "\"EventVersion\" > 0");
            table.HasCheckConstraint("CK_UserAuthorizationStates_Role", "\"Role\" IN ('User', 'Administrator')");
            table.HasCheckConstraint("CK_UserAuthorizationStates_State", "\"State\" IN ('Active', 'Blocked', 'Deleted')");
        });
        identity.HasKey(x => x.UserId);
        identity.Property(x => x.UserId).ValueGeneratedNever();
        identity.Property(x => x.Role).HasConversion<string>().HasMaxLength(20);
        identity.Property(x => x.State).HasConversion<string>().HasMaxLength(20);
        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();
        var library = modelBuilder.Entity<LibraryEntry>();
        library.ToTable("LibraryEntries", table =>
        {
            table.HasCheckConstraint("CK_LibraryEntries_AcquisitionPrice_NonNegative", "\"AcquisitionPrice\" >= 0");
            table.HasCheckConstraint("CK_LibraryEntries_Identities", "\"UserId\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"GameId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
        });
        library.HasKey(x => new { x.UserId, x.GameId }).HasName("PK_LibraryEntries");
        library.Property(x => x.AcquisitionPrice).HasPrecision(18, LibraryEntry.PriceScale);
        library.Property(x => x.AcquiredAtUtc).HasColumnType("timestamp with time zone");
        library.HasOne<Game>().WithMany().HasForeignKey(x => x.GameId).OnDelete(DeleteBehavior.Restrict);
        library.HasOne<Order>().WithMany().HasForeignKey(x => x.OriginOrderId).OnDelete(DeleteBehavior.Restrict);
        library.HasIndex(x => x.OriginOrderId).IsUnique().HasFilter("\"OriginOrderId\" IS NOT NULL");
        modelBuilder.Entity<MessageConflict>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.Kind, x.BusinessId, x.EventId, x.MessageType, x.PayloadHash }).IsUnique();
            entity.Property(x => x.Kind).HasMaxLength(40).IsRequired();
            entity.Property(x => x.MessageType).HasMaxLength(80).IsRequired();
            entity.Property(x => x.Reason).HasMaxLength(80).IsRequired();
            entity.Property(x => x.Payload).IsRequired();
            entity.Property(x => x.PayloadHash).HasMaxLength(64).IsRequired();
        });
        modelBuilder.Entity<PaymentRecoveryAttempt>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.ConflictId, x.AttemptedAtUtc });
            entity.Property(x => x.Outcome).HasMaxLength(30).IsRequired();
            entity.Property(x => x.Reason).HasMaxLength(80);
            entity.HasOne<MessageConflict>().WithMany().HasForeignKey(x => x.ConflictId).OnDelete(DeleteBehavior.Restrict);
        });
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
