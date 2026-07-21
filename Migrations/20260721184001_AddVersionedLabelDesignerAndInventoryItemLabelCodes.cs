using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntregasApi.Migrations
{
    /// <inheritdoc />
    public partial class AddVersionedLabelDesignerAndInventoryItemLabelCodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LabelCode",
                table: "InventoryItems",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            // Cada renglón histórico recibe un código interno determinista antes de
            // imponer la unicidad. Nunca usamos un valor por defecto compartido,
            // porque haría fallar el índice único en bases con inventario existente.
            migrationBuilder.Sql("""
                UPDATE "InventoryItems"
                SET "LabelCode" = 'RBI' || UPPER(REPLACE("Id"::text, '-', ''))
                WHERE "LabelCode" IS NULL OR "LabelCode" = '';
                """);

            migrationBuilder.AlterColumn<string>(
                name: "LabelCode",
                table: "InventoryItems",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(40)",
                oldMaxLength: 40,
                oldNullable: true);

            migrationBuilder.CreateTable(
                name: "LabelAssets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    OriginalFileName = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Url = table.Column<string>(type: "character varying(1200)", maxLength: 1200, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    IsArchived = table.Column<bool>(type: "boolean", nullable: false),
                    UploadedBy = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    UploadedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ArchivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LabelAssets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LabelPrintEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LabelTemplateVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetKind = table.Column<int>(type: "integer", nullable: false),
                    TargetId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PrinterProfile = table.Column<int>(type: "integer", nullable: false),
                    Method = table.Column<int>(type: "integer", nullable: false),
                    Copies = table.Column<int>(type: "integer", nullable: false),
                    RequestedBy = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LabelPrintEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LabelTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Description = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    PrinterProfile = table.Column<int>(type: "integer", nullable: false),
                    IsArchived = table.Column<bool>(type: "boolean", nullable: false),
                    PublishedVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ArchivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LabelTemplates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LabelTemplateVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LabelTemplateId = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionNumber = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    DesignJson = table.Column<string>(type: "jsonb", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    PublishedBy = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PublishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LabelTemplateVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LabelTemplateVersions_LabelTemplates_LabelTemplateId",
                        column: x => x.LabelTemplateId,
                        principalTable: "LabelTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryItems_LabelCode",
                table: "InventoryItems",
                column: "LabelCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LabelAssets_IsArchived_UploadedAt",
                table: "LabelAssets",
                columns: new[] { "IsArchived", "UploadedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_LabelPrintEvents_LabelTemplateVersionId",
                table: "LabelPrintEvents",
                column: "LabelTemplateVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_LabelPrintEvents_TargetKind_TargetId_RequestedAt",
                table: "LabelPrintEvents",
                columns: new[] { "TargetKind", "TargetId", "RequestedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_LabelTemplates_Kind_PrinterProfile_IsArchived",
                table: "LabelTemplates",
                columns: new[] { "Kind", "PrinterProfile", "IsArchived" });

            migrationBuilder.CreateIndex(
                name: "IX_LabelTemplates_PublishedVersionId",
                table: "LabelTemplates",
                column: "PublishedVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_LabelTemplateVersions_LabelTemplateId_Status",
                table: "LabelTemplateVersions",
                columns: new[] { "LabelTemplateId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_LabelTemplateVersions_LabelTemplateId_VersionNumber",
                table: "LabelTemplateVersions",
                columns: new[] { "LabelTemplateId", "VersionNumber" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_LabelPrintEvents_LabelTemplateVersions_LabelTemplateVersion~",
                table: "LabelPrintEvents",
                column: "LabelTemplateVersionId",
                principalTable: "LabelTemplateVersions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_LabelTemplates_LabelTemplateVersions_PublishedVersionId",
                table: "LabelTemplates",
                column: "PublishedVersionId",
                principalTable: "LabelTemplateVersions",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_LabelTemplates_LabelTemplateVersions_PublishedVersionId",
                table: "LabelTemplates");

            migrationBuilder.DropTable(
                name: "LabelAssets");

            migrationBuilder.DropTable(
                name: "LabelPrintEvents");

            migrationBuilder.DropTable(
                name: "LabelTemplateVersions");

            migrationBuilder.DropTable(
                name: "LabelTemplates");

            migrationBuilder.DropIndex(
                name: "IX_InventoryItems_LabelCode",
                table: "InventoryItems");

            migrationBuilder.DropColumn(
                name: "LabelCode",
                table: "InventoryItems");
        }
    }
}
