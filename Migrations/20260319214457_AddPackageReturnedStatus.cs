using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntregasApi.Migrations
{
    /// <inheritdoc />
    public partial class AddPackageReturnedStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 20260319182010_AddAlternativeAddress ya agrega esta misma columna; en una base nueva
            // el AddColumn directo fallaba con "column already exists". En producción esta migración
            // ya está aplicada y no se vuelve a ejecutar, así que solo se vuelve idempotente.
            migrationBuilder.Sql(
                "ALTER TABLE \"OrderPackages\" ADD COLUMN IF NOT EXISTS \"ReturnedAt\" timestamp with time zone;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE \"OrderPackages\" DROP COLUMN IF EXISTS \"ReturnedAt\";");
        }
    }
}
