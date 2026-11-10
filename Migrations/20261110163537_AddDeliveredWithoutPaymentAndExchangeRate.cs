using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntregasApi.Migrations
{
    /// <inheritdoc />
    public partial class AddDeliveredWithoutPaymentAndExchangeRate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "DeliveredWithoutPayment",
                table: "Orders",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeliveredWithoutPaymentAt",
                table: "Orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UnpaidDeliveryReason",
                table: "Orders",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "DeliveredWithoutPayment",
                table: "Deliveries",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "UnpaidReason",
                table: "Deliveries",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "DailyExchangeRate",
                table: "AppSettings",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<DateTime>(
                name: "ExchangeRateUpdatedAt",
                table: "AppSettings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "AppSettings",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "DailyExchangeRate", "ExchangeRateUpdatedAt" },
                values: new object[] { 18.0m, null });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeliveredWithoutPayment",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "DeliveredWithoutPaymentAt",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "UnpaidDeliveryReason",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "DeliveredWithoutPayment",
                table: "Deliveries");

            migrationBuilder.DropColumn(
                name: "UnpaidReason",
                table: "Deliveries");

            migrationBuilder.DropColumn(
                name: "DailyExchangeRate",
                table: "AppSettings");

            migrationBuilder.DropColumn(
                name: "ExchangeRateUpdatedAt",
                table: "AppSettings");
        }
    }
}
