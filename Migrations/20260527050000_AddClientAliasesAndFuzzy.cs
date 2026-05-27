using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntregasApi.Migrations
{
    /// <inheritdoc />
    public partial class AddClientAliasesAndFuzzy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Extensión necesaria para fuzzy matching por trigramas
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS pg_trgm;");

            // Columnas normalizadas en Clients
            migrationBuilder.AddColumn<string>(
                name: "NormalizedName",
                table: "Clients",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "NormalizedPhone",
                table: "Clients",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NormalizedAddress",
                table: "Clients",
                type: "text",
                nullable: true);

            // Tabla ClientAliases
            migrationBuilder.CreateTable(
                name: "ClientAliases",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ClientId = table.Column<int>(type: "integer", nullable: false),
                    Alias = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    NormalizedAlias = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Source = table.Column<int>(type: "integer", nullable: false),
                    TimesSeen = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientAliases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientAliases_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Clients_NormalizedPhone",
                table: "Clients",
                column: "NormalizedPhone");

            migrationBuilder.CreateIndex(
                name: "IX_ClientAliases_ClientId",
                table: "ClientAliases",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientAliases_NormalizedAlias",
                table: "ClientAliases",
                column: "NormalizedAlias",
                unique: true);

            // Índices GIN trigram para fuzzy matching
            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS \"IX_Clients_NormalizedName_trgm\" " +
                "ON \"Clients\" USING gin (\"NormalizedName\" gin_trgm_ops);");

            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS \"IX_Clients_NormalizedAddress_trgm\" " +
                "ON \"Clients\" USING gin (\"NormalizedAddress\" gin_trgm_ops);");

            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS \"IX_ClientAliases_NormalizedAlias_trgm\" " +
                "ON \"ClientAliases\" USING gin (\"NormalizedAlias\" gin_trgm_ops);");

            // Backfill: las columnas NormalizedName/Phone/Address se llenan en Program.cs
            // después del MigrateAsync(), porque la normalización es lógica C# (sin acentos,
            // lowercase, etc.) y no replicarla en SQL evita divergencias.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_ClientAliases_NormalizedAlias_trgm\";");
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_Clients_NormalizedAddress_trgm\";");
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_Clients_NormalizedName_trgm\";");

            migrationBuilder.DropTable(name: "ClientAliases");

            migrationBuilder.DropIndex(
                name: "IX_Clients_NormalizedPhone",
                table: "Clients");

            migrationBuilder.DropColumn(name: "NormalizedAddress", table: "Clients");
            migrationBuilder.DropColumn(name: "NormalizedPhone", table: "Clients");
            migrationBuilder.DropColumn(name: "NormalizedName", table: "Clients");
        }
    }
}
