using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace EntregasApi.Migrations
{
    /// <inheritdoc />
    public partial class AddRouteCashSettlementAndTandaRoute : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "delivery_id",
                table: "payments",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "delivery_route_id",
                table: "payments",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "registered_by",
                table: "payments",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "FinalCashActual",
                table: "DeliveryRoutes",
                type: "numeric(12,2)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FinalCashBreakdownJson",
                table: "DeliveryRoutes",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "InitialCash",
                table: "DeliveryRoutes",
                type: "numeric(12,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "InitialCashBreakdownJson",
                table: "DeliveryRoutes",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "InitialExchangeRate",
                table: "DeliveryRoutes",
                type: "numeric(12,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<DateTime>(
                name: "SettledAt",
                table: "DeliveryRoutes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "SettlementDifference",
                table: "DeliveryRoutes",
                type: "numeric(12,2)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SettlementNotes",
                table: "DeliveryRoutes",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "route_extra_incomes",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    delivery_route_id = table.Column<int>(type: "integer", nullable: false),
                    delivery_id = table.Column<int>(type: "integer", nullable: true),
                    amount = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    payment_method = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    income_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    client_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_route_extra_incomes", x => x.id);
                    table.ForeignKey(
                        name: "FK_route_extra_incomes_DeliveryRoutes_delivery_route_id",
                        column: x => x.delivery_route_id,
                        principalTable: "DeliveryRoutes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_payments_delivery_route_id",
                table: "payments",
                column: "delivery_route_id");

            migrationBuilder.CreateIndex(
                name: "IX_route_extra_incomes_delivery_route_id",
                table: "route_extra_incomes",
                column: "delivery_route_id");

            migrationBuilder.AddForeignKey(
                name: "FK_payments_DeliveryRoutes_delivery_route_id",
                table: "payments",
                column: "delivery_route_id",
                principalTable: "DeliveryRoutes",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_payments_DeliveryRoutes_delivery_route_id",
                table: "payments");

            migrationBuilder.DropTable(
                name: "route_extra_incomes");

            migrationBuilder.DropIndex(
                name: "IX_payments_delivery_route_id",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "delivery_id",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "delivery_route_id",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "registered_by",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "FinalCashActual",
                table: "DeliveryRoutes");

            migrationBuilder.DropColumn(
                name: "FinalCashBreakdownJson",
                table: "DeliveryRoutes");

            migrationBuilder.DropColumn(
                name: "InitialCash",
                table: "DeliveryRoutes");

            migrationBuilder.DropColumn(
                name: "InitialCashBreakdownJson",
                table: "DeliveryRoutes");

            migrationBuilder.DropColumn(
                name: "InitialExchangeRate",
                table: "DeliveryRoutes");

            migrationBuilder.DropColumn(
                name: "SettledAt",
                table: "DeliveryRoutes");

            migrationBuilder.DropColumn(
                name: "SettlementDifference",
                table: "DeliveryRoutes");

            migrationBuilder.DropColumn(
                name: "SettlementNotes",
                table: "DeliveryRoutes");
        }
    }
}
