using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mimir.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddFriendKeyAndFriendships : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FriendKey",
                table: "users",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "friendships",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RequesterId = table.Column<Guid>(type: "uuid", nullable: false),
                    AddresseeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    RespondedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_friendships", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_users_FriendKey",
                table: "users",
                column: "FriendKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_friendships_AddresseeId_RequesterId",
                table: "friendships",
                columns: new[] { "AddresseeId", "RequesterId" });

            migrationBuilder.CreateIndex(
                name: "IX_friendships_RequesterId_AddresseeId",
                table: "friendships",
                columns: new[] { "RequesterId", "AddresseeId" });

            migrationBuilder.CreateIndex(
                name: "IX_friendships_Status",
                table: "friendships",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "friendships");

            migrationBuilder.DropIndex(
                name: "IX_users_FriendKey",
                table: "users");

            migrationBuilder.DropColumn(
                name: "FriendKey",
                table: "users");
        }
    }
}
