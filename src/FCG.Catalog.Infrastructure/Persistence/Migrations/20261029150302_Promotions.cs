using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FCG.Catalog.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Promotions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Promotions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GameId = table.Column<Guid>(type: "uuid", nullable: false),
                    DiscountPercentage = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    StartsAtUtc = table.Column<DateTime>(type: "timestamp(6) with time zone", precision: 6, nullable: false),
                    EndsAtUtc = table.Column<DateTime>(type: "timestamp(6) with time zone", precision: 6, nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Promotions", x => x.Id);
                    table.CheckConstraint("CK_Promotions_Creator", "\"CreatedByUserId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("CK_Promotions_DateRange", "\"EndsAtUtc\" > \"StartsAtUtc\"");
                    table.CheckConstraint("CK_Promotions_DiscountPercentage_Range", "\"DiscountPercentage\" > 0 AND \"DiscountPercentage\" <= 100");
                    table.ForeignKey(
                        name: "FK_Promotions_Games_GameId",
                        column: x => x.GameId,
                        principalTable: "Games",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Promotions_CreatedByUserId",
                table: "Promotions",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Promotions_GameId_StartsAtUtc_EndsAtUtc",
                table: "Promotions",
                columns: new[] { "GameId", "StartsAtUtc", "EndsAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Promotions");
        }
    }
}
