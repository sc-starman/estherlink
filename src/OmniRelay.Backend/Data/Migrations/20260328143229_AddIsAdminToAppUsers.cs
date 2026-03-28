using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OmniRelay.Backend.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddIsAdminToAppUsers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_admin",
                schema: "public",
                table: "app_users",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "is_admin",
                schema: "public",
                table: "app_users");
        }
    }
}
