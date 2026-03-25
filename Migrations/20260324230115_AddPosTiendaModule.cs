using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace EntregasApi.Migrations
{
    /// <inheritdoc />
    public partial class AddPosTiendaModule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Category",
                table: "Products");

            migrationBuilder.RenameColumn(
                name: "DefaultPrice",
                table: "Products",
                newName: "Price");

            migrationBuilder.AddColumn<string>(
                name: "SKU",
                table: "Products",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "CashRegisterSessionId",
                table: "OrderPayments",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProductId",
                table: "OrderItems",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CashRegisterSessions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    OpeningTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ClosingTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    InitialCash = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    FinalCashExpected = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    FinalCashActual = table.Column<decimal>(type: "numeric(12,2)", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CashRegisterSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CashRegisterSessions_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Products_SKU",
                table: "Products",
                column: "SKU",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrderPayments_CashRegisterSessionId",
                table: "OrderPayments",
                column: "CashRegisterSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderItems_ProductId",
                table: "OrderItems",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_CashRegisterSessions_UserId",
                table: "CashRegisterSessions",
                column: "UserId");

            migrationBuilder.AddForeignKey(
                name: "FK_OrderItems_Products_ProductId",
                table: "OrderItems",
                column: "ProductId",
                principalTable: "Products",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_OrderPayments_CashRegisterSessions_CashRegisterSessionId",
                table: "OrderPayments",
                column: "CashRegisterSessionId",
                principalTable: "CashRegisterSessions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_OrderItems_Products_ProductId",
                table: "OrderItems");

            migrationBuilder.DropForeignKey(
                name: "FK_OrderPayments_CashRegisterSessions_CashRegisterSessionId",
                table: "OrderPayments");

            migrationBuilder.DropTable(
                name: "CashRegisterSessions");

            migrationBuilder.DropIndex(
                name: "IX_Products_SKU",
                table: "Products");

            migrationBuilder.DropIndex(
                name: "IX_OrderPayments_CashRegisterSessionId",
                table: "OrderPayments");

            migrationBuilder.DropIndex(
                name: "IX_OrderItems_ProductId",
                table: "OrderItems");

            migrationBuilder.DropColumn(
                name: "SKU",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "CashRegisterSessionId",
                table: "OrderPayments");

            migrationBuilder.DropColumn(
                name: "ProductId",
                table: "OrderItems");

            migrationBuilder.RenameColumn(
                name: "Price",
                table: "Products",
                newName: "DefaultPrice");

            migrationBuilder.AddColumn<string>(
                name: "Category",
                table: "Products",
                type: "text",
                nullable: true);
        }
    }
}
