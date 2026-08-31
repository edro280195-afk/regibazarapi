using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntregasApi.Migrations
{
    /// <inheritdoc />
    public partial class AddTandaPaymentProofs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "public_access_token",
                table: "tanda_participants",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.Sql(
                "UPDATE tanda_participants SET public_access_token = md5(random()::text || clock_timestamp()::text || id::text) WHERE public_access_token IS NULL;");

            migrationBuilder.AlterColumn<string>(
                name: "public_access_token",
                table: "tanda_participants",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64,
                oldNullable: true);

            migrationBuilder.CreateTable(
                name: "tanda_payment_proofs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    participant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tanda_id = table.Column<Guid>(type: "uuid", nullable: false),
                    week_number = table.Column<int>(type: "integer", nullable: false),
                    amount_claimed = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    file_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    file_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    submitted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    reviewed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    reviewed_by = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    rejection_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    registered_payment_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tanda_payment_proofs", x => x.id);
                    table.ForeignKey(
                        name: "FK_tanda_payment_proofs_payments_registered_payment_id",
                        column: x => x.registered_payment_id,
                        principalTable: "payments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_tanda_payment_proofs_tanda_participants_participant_id",
                        column: x => x.participant_id,
                        principalTable: "tanda_participants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_tanda_payment_proofs_tandas_tanda_id",
                        column: x => x.tanda_id,
                        principalTable: "tandas",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TandaParticipant_PublicAccessToken",
                table: "tanda_participants",
                column: "public_access_token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tanda_payment_proofs_registered_payment_id",
                table: "tanda_payment_proofs",
                column: "registered_payment_id");

            migrationBuilder.CreateIndex(
                name: "IX_tanda_payment_proofs_tanda_id",
                table: "tanda_payment_proofs",
                column: "tanda_id");

            migrationBuilder.CreateIndex(
                name: "IX_TandaPaymentProof_Participant_Week",
                table: "tanda_payment_proofs",
                columns: new[] { "participant_id", "week_number" });

            migrationBuilder.CreateIndex(
                name: "IX_TandaPaymentProof_Status",
                table: "tanda_payment_proofs",
                column: "status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tanda_payment_proofs");

            migrationBuilder.DropIndex(
                name: "IX_TandaParticipant_PublicAccessToken",
                table: "tanda_participants");

            migrationBuilder.DropColumn(
                name: "public_access_token",
                table: "tanda_participants");
        }
    }
}
