using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OmniRelay.Backend.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDiscountCouponsAndOrderDiscountSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "base_fiat_amount",
                schema: "public",
                table: "commerce_orders",
                type: "numeric(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "discount_amount",
                schema: "public",
                table: "commerce_orders",
                type: "numeric(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "discount_code",
                schema: "public",
                table: "commerce_orders",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "discount_consumed",
                schema: "public",
                table: "commerce_orders",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "discount_coupon_id",
                schema: "public",
                table: "commerce_orders",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "discount_percent",
                schema: "public",
                table: "commerce_orders",
                type: "integer",
                nullable: true);

            migrationBuilder.Sql(
                "UPDATE public.commerce_orders SET base_fiat_amount = fiat_amount;");

            migrationBuilder.Sql(
                "UPDATE public.commerce_orders SET discount_amount = 0;");

            migrationBuilder.AlterColumn<decimal>(
                name: "base_fiat_amount",
                schema: "public",
                table: "commerce_orders",
                type: "numeric(18,2)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,2)",
                oldDefaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "discount_coupons",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    normalized_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    discount_percent = table.Column<int>(type: "integer", nullable: false),
                    max_uses = table.Column<int>(type: "integer", nullable: false),
                    times_redeemed = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    disabled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_discount_coupons", x => x.Id);
                    table.CheckConstraint("ck_discount_coupons_max_uses_positive", "max_uses > 0");
                    table.CheckConstraint("ck_discount_coupons_percent_range", "discount_percent >= 1 AND discount_percent <= 99");
                    table.CheckConstraint("ck_discount_coupons_times_redeemed_non_negative", "times_redeemed >= 0");
                    table.ForeignKey(
                        name: "FK_discount_coupons_app_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalSchema: "public",
                        principalTable: "app_users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_commerce_orders_discount_coupon_id",
                schema: "public",
                table: "commerce_orders",
                column: "discount_coupon_id");

            migrationBuilder.CreateIndex(
                name: "IX_discount_coupons_created_at",
                schema: "public",
                table: "discount_coupons",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "IX_discount_coupons_created_by_user_id",
                schema: "public",
                table: "discount_coupons",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_discount_coupons_normalized_code",
                schema: "public",
                table: "discount_coupons",
                column: "normalized_code",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_commerce_orders_discount_coupons_discount_coupon_id",
                schema: "public",
                table: "commerce_orders",
                column: "discount_coupon_id",
                principalSchema: "public",
                principalTable: "discount_coupons",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_commerce_orders_discount_coupons_discount_coupon_id",
                schema: "public",
                table: "commerce_orders");

            migrationBuilder.DropTable(
                name: "discount_coupons",
                schema: "public");

            migrationBuilder.DropIndex(
                name: "IX_commerce_orders_discount_coupon_id",
                schema: "public",
                table: "commerce_orders");

            migrationBuilder.DropColumn(
                name: "base_fiat_amount",
                schema: "public",
                table: "commerce_orders");

            migrationBuilder.DropColumn(
                name: "discount_amount",
                schema: "public",
                table: "commerce_orders");

            migrationBuilder.DropColumn(
                name: "discount_code",
                schema: "public",
                table: "commerce_orders");

            migrationBuilder.DropColumn(
                name: "discount_consumed",
                schema: "public",
                table: "commerce_orders");

            migrationBuilder.DropColumn(
                name: "discount_coupon_id",
                schema: "public",
                table: "commerce_orders");

            migrationBuilder.DropColumn(
                name: "discount_percent",
                schema: "public",
                table: "commerce_orders");
        }
    }
}
