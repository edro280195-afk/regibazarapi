using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntregasApi.Migrations
{
    /// <inheritdoc />
    public partial class AddLabelTemplateDefaultSelection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsDefault",
                table: "LabelTemplates",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Al actualizar una instalación que ya tenía plantillas, conserva una
            // ruta de impresión determinista: la publicada más reciente de cada
            // tipo se convierte en predeterminada.
            migrationBuilder.Sql(@"
                WITH ranked AS (
                    SELECT ""Id"", ROW_NUMBER() OVER (
                        PARTITION BY ""Kind""
                        ORDER BY ""UpdatedAt"" DESC, ""CreatedAt"" DESC
                    ) AS row_number
                    FROM ""LabelTemplates""
                    WHERE NOT ""IsArchived"" AND ""PublishedVersionId"" IS NOT NULL
                )
                UPDATE ""LabelTemplates"" AS templates
                SET ""IsDefault"" = true
                FROM ranked
                WHERE templates.""Id"" = ranked.""Id"" AND ranked.row_number = 1;");

            migrationBuilder.CreateIndex(
                name: "IX_LabelTemplates_ActiveDefaultByKind",
                table: "LabelTemplates",
                column: "Kind",
                unique: true,
                filter: "\"IsDefault\" = true AND \"IsArchived\" = false");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LabelTemplates_ActiveDefaultByKind",
                table: "LabelTemplates");

            migrationBuilder.DropColumn(
                name: "IsDefault",
                table: "LabelTemplates");
        }
    }
}
