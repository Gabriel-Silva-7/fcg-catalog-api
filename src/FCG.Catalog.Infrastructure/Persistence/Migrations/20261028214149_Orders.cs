using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FCG.Catalog.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Orders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Orders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    GameId = table.Column<Guid>(type: "uuid", nullable: false),
                    Price = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    PlacedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RejectionReason = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Orders", x => x.Id);
                    table.CheckConstraint("CK_Orders_Identities", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"UserId\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"GameId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("CK_Orders_Price", "\"Price\" >= 0");
                    table.CheckConstraint("CK_Orders_Result", "(\"Status\" = 'Pending' AND \"CompletedAtUtc\" IS NULL AND \"RejectionReason\" IS NULL AND \"Version\" = 1) OR (\"Status\" = 'Approved' AND \"CompletedAtUtc\" IS NOT NULL AND \"RejectionReason\" IS NULL AND \"Version\" = 2) OR (\"Status\" = 'Rejected' AND \"CompletedAtUtc\" IS NOT NULL AND \"RejectionReason\" IS NOT NULL AND \"RejectionReason\" = 'AmountAboveLimit' AND \"Version\" = 2)");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Orders_UserId_PlacedAtUtc_Id",
                table: "Orders",
                columns: new[] { "UserId", "PlacedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "UX_Orders_OpenUserGame",
                table: "Orders",
                columns: new[] { "UserId", "GameId" },
                unique: true,
                filter: "\"Status\" IN ('Pending', 'Approved')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Orders");
        }
    }
}
