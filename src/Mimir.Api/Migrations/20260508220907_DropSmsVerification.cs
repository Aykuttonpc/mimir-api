using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mimir.Api.Migrations
{
    /// <inheritdoc />
    public partial class DropSmsVerification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_otp_codes_UserId_Type",
                table: "otp_codes");

            migrationBuilder.DropColumn(
                name: "Type",
                table: "otp_codes");

            migrationBuilder.AlterColumn<string>(
                name: "Phone",
                table: "users",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20);

            migrationBuilder.CreateIndex(
                name: "IX_otp_codes_UserId",
                table: "otp_codes",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_otp_codes_UserId",
                table: "otp_codes");

            migrationBuilder.AlterColumn<string>(
                name: "Phone",
                table: "users",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20,
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Type",
                table: "otp_codes",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_otp_codes_UserId_Type",
                table: "otp_codes",
                columns: new[] { "UserId", "Type" });
        }
    }
}
