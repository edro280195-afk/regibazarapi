using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace EntregasApi.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderConsolidations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ConsolidationOwnerClientId",
                table: "Orders",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConsolidationOwnerNameSnapshot",
                table: "Orders",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OrderConsolidationId",
                table: "Orders",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "OrderConsolidations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PrimaryOrderId = table.Column<int>(type: "integer", nullable: false),
                    PrimaryClientId = table.Column<int>(type: "integer", nullable: false),
                    PrimaryClientNameSnapshot = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PrimaryAddressSnapshot = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    PrimaryLatitude = table.Column<double>(type: "double precision", nullable: true),
                    PrimaryLongitude = table.Column<double>(type: "double precision", nullable: true),
                    DeliveryInstructionsSnapshot = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderConsolidations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Orders_OrderConsolidationId",
                table: "Orders",
                column: "OrderConsolidationId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderConsolidations_CreatedAtUtc",
                table: "OrderConsolidations",
                column: "CreatedAtUtc");

            migrationBuilder.AddForeignKey(
                name: "FK_Orders_OrderConsolidations_OrderConsolidationId",
                table: "Orders",
                column: "OrderConsolidationId",
                principalTable: "OrderConsolidations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Orders_OrderConsolidations_OrderConsolidationId",
                table: "Orders");

            migrationBuilder.DropTable(
                name: "OrderConsolidations");

            migrationBuilder.DropIndex(
                name: "IX_Orders_OrderConsolidationId",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "ConsolidationOwnerClientId",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "ConsolidationOwnerNameSnapshot",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "OrderConsolidationId",
                table: "Orders");
        }
    }
}
