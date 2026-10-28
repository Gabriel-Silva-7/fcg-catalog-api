using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FCG.Catalog.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PaymentApplications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LibraryEntries",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    GameId = table.Column<Guid>(type: "uuid", nullable: false),
                    AcquiredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AcquisitionPrice = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    OriginOrderId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LibraryEntries", x => new { x.UserId, x.GameId });
                    table.CheckConstraint("CK_LibraryEntries_AcquisitionPrice_NonNegative", "\"AcquisitionPrice\" >= 0");
                    table.CheckConstraint("CK_LibraryEntries_Identities", "\"UserId\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"GameId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.ForeignKey(
                        name: "FK_LibraryEntries_Games_GameId",
                        column: x => x.GameId,
                        principalTable: "Games",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LibraryEntries_Orders_OriginOrderId",
                        column: x => x.OriginOrderId,
                        principalTable: "Orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MessageConflicts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    TransportMessageId = table.Column<Guid>(type: "uuid", nullable: true),
                    EventVersion = table.Column<long>(type: "bigint", nullable: true),
                    Reason = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Payload = table.Column<string>(type: "text", nullable: false),
                    PayloadHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FirstSeenAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Observations = table.Column<int>(type: "integer", nullable: false),
                    Resolved = table.Column<bool>(type: "boolean", nullable: false),
                    ResolvedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageConflicts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PaymentRecoveryAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConflictId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttemptedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Outcome = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Reason = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentRecoveryAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaymentRecoveryAttempts_MessageConflicts_ConflictId",
                        column: x => x.ConflictId,
                        principalTable: "MessageConflicts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LibraryEntries_GameId",
                table: "LibraryEntries",
                column: "GameId");

            migrationBuilder.CreateIndex(
                name: "IX_LibraryEntries_OriginOrderId",
                table: "LibraryEntries",
                column: "OriginOrderId",
                unique: true,
                filter: "\"OriginOrderId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MessageConflicts_Kind_BusinessId_EventId_MessageType_Payloa~",
                table: "MessageConflicts",
                columns: new[] { "Kind", "BusinessId", "EventId", "MessageType", "PayloadHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentRecoveryAttempts_ConflictId_AttemptedAtUtc",
                table: "PaymentRecoveryAttempts",
                columns: new[] { "ConflictId", "AttemptedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LibraryEntries");

            migrationBuilder.DropTable(
                name: "PaymentRecoveryAttempts");

            migrationBuilder.DropTable(
                name: "MessageConflicts");
        }
    }
}
