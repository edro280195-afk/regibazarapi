using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntregasApi.Migrations
{
    /// <inheritdoc />
    public partial class AddCashPriceToTandaProduct : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "deposit_date",
                table: "tanda_payment_proofs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ocr_amount",
                table: "tanda_payment_proofs",
                type: "numeric(12,2)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ocr_confidence",
                table: "tanda_payment_proofs",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ocr_text",
                table: "tanda_payment_proofs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "cash_price",
                table: "products",
                type: "numeric(12,2)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "deposit_date",
                table: "payments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ocr_amount",
                table: "payments",
                type: "numeric(12,2)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ocr_confidence",
                table: "payments",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ocr_text",
                table: "payments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "payment_method",
                table: "payments",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "proof_url",
                table: "payments",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "tanda_participant_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    participant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: true),
                    product_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    unit_price = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    weekly_amount = table.Column<decimal>(type: "numeric(12,2)", nullable: true),
                    variant = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tanda_participant_items", x => x.id);
                    table.ForeignKey(
                        name: "FK_tanda_participant_items_products_product_id",
                        column: x => x.product_id,
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_tanda_participant_items_tanda_participants_participant_id",
                        column: x => x.participant_id,
                        principalTable: "tanda_participants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_tanda_participant_items_participant_id",
                table: "tanda_participant_items",
                column: "participant_id");

            migrationBuilder.CreateIndex(
                name: "IX_tanda_participant_items_product_id",
                table: "tanda_participant_items",
                column: "product_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tanda_participant_items");

            migrationBuilder.DropColumn(
                name: "deposit_date",
                table: "tanda_payment_proofs");

            migrationBuilder.DropColumn(
                name: "ocr_amount",
                table: "tanda_payment_proofs");

            migrationBuilder.DropColumn(
                name: "ocr_confidence",
                table: "tanda_payment_proofs");

            migrationBuilder.DropColumn(
                name: "ocr_text",
                table: "tanda_payment_proofs");

            migrationBuilder.DropColumn(
                name: "cash_price",
                table: "products");

            migrationBuilder.DropColumn(
                name: "deposit_date",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "ocr_amount",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "ocr_confidence",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "ocr_text",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "payment_method",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "proof_url",
                table: "payments");
        }
    }
}
