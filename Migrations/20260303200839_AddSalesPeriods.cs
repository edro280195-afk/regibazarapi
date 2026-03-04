using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace EntregasApi.Migrations
{
    /// <inheritdoc />
    public partial class AddSalesPeriods : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SalesPeriodId",
                table: "Orders",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SalesPeriodId",
                table: "Investments",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SalesPeriods",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    StartDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesPeriods", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Orders_SalesPeriodId",
                table: "Orders",
                column: "SalesPeriodId");

            migrationBuilder.CreateIndex(
                name: "IX_Investments_SalesPeriodId",
                table: "Investments",
                column: "SalesPeriodId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesPeriods_IsActive",
                table: "SalesPeriods",
                column: "IsActive");

            migrationBuilder.AddForeignKey(
                name: "FK_Investments_SalesPeriods_SalesPeriodId",
                table: "Investments",
                column: "SalesPeriodId",
                principalTable: "SalesPeriods",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Orders_SalesPeriods_SalesPeriodId",
                table: "Orders",
                column: "SalesPeriodId",
                principalTable: "SalesPeriods",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Investments_SalesPeriods_SalesPeriodId",
                table: "Investments");

            migrationBuilder.DropForeignKey(
                name: "FK_Orders_SalesPeriods_SalesPeriodId",
                table: "Orders");

            migrationBuilder.DropTable(
                name: "SalesPeriods");

            migrationBuilder.DropIndex(
                name: "IX_Orders_SalesPeriodId",
                table: "Orders");

            migrationBuilder.DropIndex(
                name: "IX_Investments_SalesPeriodId",
                table: "Investments");

            migrationBuilder.DropColumn(
                name: "SalesPeriodId",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "SalesPeriodId",
                table: "Investments");
        }
    }
}
