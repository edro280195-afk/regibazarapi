using System;
using EntregasApi.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntregasApi.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260917090000_AddTandaMixedItemsAndPaymentProofs")]
public partial class AddTandaMixedItemsAndPaymentProofs : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTime>("deposit_date", "payments", "timestamp with time zone", nullable: true);
        migrationBuilder.AddColumn<decimal>("ocr_amount", "payments", "numeric(12,2)", nullable: true);
        migrationBuilder.AddColumn<decimal>("ocr_confidence", "payments", "numeric", nullable: true);
        migrationBuilder.AddColumn<string>("ocr_text", "payments", "text", nullable: true);
        migrationBuilder.AddColumn<string>("payment_method", "payments", "character varying(50)", maxLength: 50, nullable: true);
        migrationBuilder.AddColumn<string>("proof_url", "payments", "character varying(1000)", maxLength: 1000, nullable: true);

        migrationBuilder.AddColumn<DateTime>("deposit_date", "tanda_payment_proofs", "timestamp with time zone", nullable: true);
        migrationBuilder.AddColumn<decimal>("ocr_amount", "tanda_payment_proofs", "numeric(12,2)", nullable: true);
        migrationBuilder.AddColumn<string>("ocr_text", "tanda_payment_proofs", "text", nullable: true);
        migrationBuilder.AddColumn<decimal>("ocr_confidence", "tanda_payment_proofs", "numeric", nullable: true);

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

        migrationBuilder.CreateIndex("IX_tanda_participant_items_participant_id", "tanda_participant_items", "participant_id");
        migrationBuilder.CreateIndex("IX_tanda_participant_items_product_id", "tanda_participant_items", "product_id");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "tanda_participant_items");
        migrationBuilder.DropColumn("deposit_date", "tanda_payment_proofs");
        migrationBuilder.DropColumn("ocr_amount", "tanda_payment_proofs");
        migrationBuilder.DropColumn("ocr_text", "tanda_payment_proofs");
        migrationBuilder.DropColumn("ocr_confidence", "tanda_payment_proofs");
        migrationBuilder.DropColumn("deposit_date", "payments");
        migrationBuilder.DropColumn("ocr_amount", "payments");
        migrationBuilder.DropColumn("ocr_confidence", "payments");
        migrationBuilder.DropColumn("ocr_text", "payments");
        migrationBuilder.DropColumn("payment_method", "payments");
        migrationBuilder.DropColumn("proof_url", "payments");
    }
}
