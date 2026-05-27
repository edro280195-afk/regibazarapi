using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntregasApi.Migrations
{
    /// <inheritdoc />
    public partial class AddTandaSupportToDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Una Delivery ahora puede apuntar a una Order O a un TandaParticipant.
            // OrderId pasa a ser nullable y el índice único queda condicionado a no-nulos.
            migrationBuilder.DropIndex(
                name: "IX_Deliveries_OrderId",
                table: "Deliveries");

            migrationBuilder.AlterColumn<int>(
                name: "OrderId",
                table: "Deliveries",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<Guid>(
                name: "TandaParticipantId",
                table: "Deliveries",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "Deliveries",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_Deliveries_OrderId",
                table: "Deliveries",
                column: "OrderId",
                unique: true,
                filter: "\"OrderId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Deliveries_TandaParticipantId",
                table: "Deliveries",
                column: "TandaParticipantId");

            migrationBuilder.AddForeignKey(
                name: "FK_Deliveries_tanda_participants_TandaParticipantId",
                table: "Deliveries",
                column: "TandaParticipantId",
                principalTable: "tanda_participants",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            // Garantiza el XOR: exactamente uno entre OrderId y TandaParticipantId.
            migrationBuilder.Sql(
                "ALTER TABLE \"Deliveries\" ADD CONSTRAINT \"CK_Deliveries_OrderXorTanda\" " +
                "CHECK ((\"OrderId\" IS NOT NULL AND \"TandaParticipantId\" IS NULL) OR " +
                "(\"OrderId\" IS NULL AND \"TandaParticipantId\" IS NOT NULL));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE \"Deliveries\" DROP CONSTRAINT IF EXISTS \"CK_Deliveries_OrderXorTanda\";");

            migrationBuilder.DropForeignKey(
                name: "FK_Deliveries_tanda_participants_TandaParticipantId",
                table: "Deliveries");

            migrationBuilder.DropIndex(
                name: "IX_Deliveries_TandaParticipantId",
                table: "Deliveries");

            migrationBuilder.DropIndex(
                name: "IX_Deliveries_OrderId",
                table: "Deliveries");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "Deliveries");

            migrationBuilder.DropColumn(
                name: "TandaParticipantId",
                table: "Deliveries");

            migrationBuilder.AlterColumn<int>(
                name: "OrderId",
                table: "Deliveries",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Deliveries_OrderId",
                table: "Deliveries",
                column: "OrderId",
                unique: true);
        }
    }
}
