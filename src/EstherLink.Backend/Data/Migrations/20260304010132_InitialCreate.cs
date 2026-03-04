using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EstherLink.Backend.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "public");

            migrationBuilder.CreateTable(
                name: "app_releases",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: false),
                    download_url = table.Column<string>(type: "text", nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    min_supported_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_releases", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "licenses",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    license_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    plan = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    max_devices = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_licenses", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "whitelist_sets",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    set_group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    country_code = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    category = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_whitelist_sets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "license_activations",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    license_id = table.Column<Guid>(type: "uuid", nullable: false),
                    fingerprint_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    is_blocked = table.Column<bool>(type: "boolean", nullable: false),
                    meta_json = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_license_activations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_license_activations_licenses_license_id",
                        column: x => x.license_id,
                        principalSchema: "public",
                        principalTable: "licenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "whitelist_entries",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    whitelist_set_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cidr = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    note = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_whitelist_entries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_whitelist_entries_whitelist_sets_whitelist_set_id",
                        column: x => x.whitelist_set_id,
                        principalSchema: "public",
                        principalTable: "whitelist_sets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_app_releases_channel_version",
                schema: "public",
                table: "app_releases",
                columns: new[] { "channel", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_license_activations_license_id_fingerprint_hash",
                schema: "public",
                table: "license_activations",
                columns: new[] { "license_id", "fingerprint_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_licenses_license_key",
                schema: "public",
                table: "licenses",
                column: "license_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_whitelist_entries_whitelist_set_id_cidr",
                schema: "public",
                table: "whitelist_entries",
                columns: new[] { "whitelist_set_id", "cidr" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_whitelist_sets_country_code_category",
                schema: "public",
                table: "whitelist_sets",
                columns: new[] { "country_code", "category" });

            migrationBuilder.CreateIndex(
                name: "IX_whitelist_sets_set_group_id_version",
                schema: "public",
                table: "whitelist_sets",
                columns: new[] { "set_group_id", "version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "app_releases",
                schema: "public");

            migrationBuilder.DropTable(
                name: "license_activations",
                schema: "public");

            migrationBuilder.DropTable(
                name: "whitelist_entries",
                schema: "public");

            migrationBuilder.DropTable(
                name: "licenses",
                schema: "public");

            migrationBuilder.DropTable(
                name: "whitelist_sets",
                schema: "public");
        }
    }
}
