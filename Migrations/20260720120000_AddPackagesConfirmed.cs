using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntregasApi.Migrations
{
    /// <inheritdoc />
    public partial class AddPackagesConfirmed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "PackagesConfirmed",
                table: "Orders",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "PackagesReminderSentAt",
                table: "Orders",
                type: "timestamp with time zone",
                nullable: true);

            // Retro-compat: los pedidos que YA tienen bolsas capturadas (número puesto o
            // bolsas con QR generadas) se marcan como confirmados para no bloquearlos ni
            // recordarlos. Los que están en null quedan como pendientes.
            migrationBuilder.Sql(
                "UPDATE \"Orders\" SET \"PackagesConfirmed\" = true WHERE \"TotalPackages\" IS NOT NULL OR \"IsFullyPacked\" = true;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PackagesReminderSentAt",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "PackagesConfirmed",
                table: "Orders");
        }
    }
}
