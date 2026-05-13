using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OmniRelay.Backend.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNewsletterCampaigns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "newsletters",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    total_users = table.Column<int>(type: "integer", nullable: false),
                    total_visited = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_newsletters", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "newsletter_clients",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    newsletter_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    visited_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    error = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    send_attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_newsletter_clients", x => x.Id);
                    table.ForeignKey(
                        name: "FK_newsletter_clients_app_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "public",
                        principalTable: "app_users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_newsletter_clients_newsletters_newsletter_id",
                        column: x => x.newsletter_id,
                        principalSchema: "public",
                        principalTable: "newsletters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_newsletter_clients_newsletter_id_email",
                schema: "public",
                table: "newsletter_clients",
                columns: new[] { "newsletter_id", "email" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_newsletter_clients_newsletter_id_state",
                schema: "public",
                table: "newsletter_clients",
                columns: new[] { "newsletter_id", "state" });

            migrationBuilder.CreateIndex(
                name: "IX_newsletter_clients_user_id",
                schema: "public",
                table: "newsletter_clients",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_newsletter_clients_visited_at",
                schema: "public",
                table: "newsletter_clients",
                column: "visited_at");

            migrationBuilder.CreateIndex(
                name: "IX_newsletters_created_at",
                schema: "public",
                table: "newsletters",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "IX_newsletters_state",
                schema: "public",
                table: "newsletters",
                column: "state");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "newsletter_clients",
                schema: "public");

            migrationBuilder.DropTable(
                name: "newsletters",
                schema: "public");
        }
    }
}
