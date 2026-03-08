using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OmniRelay.Backend.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSingleDeviceTransfers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "license_transfers",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    license_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_fingerprint_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    to_fingerprint_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    request_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    app_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    meta_json = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_license_transfers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_license_transfers_licenses_license_id",
                        column: x => x.license_id,
                        principalSchema: "public",
                        principalTable: "licenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_license_transfers_license_id_created_at",
                schema: "public",
                table: "license_transfers",
                columns: new[] { "license_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "license_transfers",
                schema: "public");
        }
    }
}
