using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LUThrift.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountEmailConfirmationRequirement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "EmailConfirmationRequiredForSignIn",
                table: "AspNetUsers",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EmailConfirmationRequiredForSignIn",
                table: "AspNetUsers");
        }
    }
}
