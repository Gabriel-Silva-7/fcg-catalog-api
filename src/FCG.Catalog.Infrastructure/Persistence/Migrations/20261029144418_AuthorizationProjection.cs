using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FCG.Catalog.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuthorizationProjection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UserAuthorizationStates",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventVersion = table.Column<long>(type: "bigint", nullable: false),
                    Role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    State = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserAuthorizationStates", x => x.UserId);
                    table.CheckConstraint("CK_UserAuthorizationStates_Identity", "\"UserId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("CK_UserAuthorizationStates_Role", "\"Role\" IN ('User', 'Administrator')");
                    table.CheckConstraint("CK_UserAuthorizationStates_State", "\"State\" IN ('Active', 'Blocked', 'Deleted')");
                    table.CheckConstraint("CK_UserAuthorizationStates_Version", "\"EventVersion\" > 0");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserAuthorizationStates");
        }
    }
}
