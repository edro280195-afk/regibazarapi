using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntregasApi.Migrations
{
    /// <inheritdoc />
    public partial class AddInventoryCountsAndBarcodeLookup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InventoryCountSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InventoryBoxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    PerformedBy = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    CountedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryCountSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InventoryCountSessions_InventoryBoxes_InventoryBoxId",
                        column: x => x.InventoryBoxId,
                        principalTable: "InventoryBoxes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "InventoryCountEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InventoryCountSessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    InventoryItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    ItemName = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    Variant = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    ExpectedQuantity = table.Column<int>(type: "integer", nullable: false),
                    ActualQuantity = table.Column<int>(type: "integer", nullable: false),
                    Difference = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryCountEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InventoryCountEntries_InventoryCountSessions_InventoryCount~",
                        column: x => x.InventoryCountSessionId,
                        principalTable: "InventoryCountSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryCountEntries_InventoryCountSessionId_InventoryItem~",
                table: "InventoryCountEntries",
                columns: new[] { "InventoryCountSessionId", "InventoryItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InventoryCountSessions_InventoryBoxId_CountedAt",
                table: "InventoryCountSessions",
                columns: new[] { "InventoryBoxId", "CountedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InventoryCountEntries");

            migrationBuilder.DropTable(
                name: "InventoryCountSessions");
        }
    }
}
