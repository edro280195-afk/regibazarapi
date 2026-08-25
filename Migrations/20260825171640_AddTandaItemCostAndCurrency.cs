using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntregasApi.Migrations
{
    /// <inheritdoc />
    public partial class AddTandaItemCostAndCurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "currency",
                table: "tandas",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<decimal>(
                name: "exchange_rate",
                table: "tandas",
                type: "numeric(12,4)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "item_cost",
                table: "tandas",
                type: "numeric(12,2)",
                nullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "weekly_amount",
                table: "tanda_participants",
                type: "numeric(12,2)",
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "numeric",
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "currency",
                table: "tanda_participants",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "exchange_rate",
                table: "tanda_participants",
                type: "numeric(12,4)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "item_cost",
                table: "tanda_participants",
                type: "numeric(12,2)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "currency",
                table: "tandas");

            migrationBuilder.DropColumn(
                name: "exchange_rate",
                table: "tandas");

            migrationBuilder.DropColumn(
                name: "item_cost",
                table: "tandas");

            migrationBuilder.DropColumn(
                name: "currency",
                table: "tanda_participants");

            migrationBuilder.DropColumn(
                name: "exchange_rate",
                table: "tanda_participants");

            migrationBuilder.DropColumn(
                name: "item_cost",
                table: "tanda_participants");

            migrationBuilder.AlterColumn<decimal>(
                name: "weekly_amount",
                table: "tanda_participants",
                type: "numeric",
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "numeric(12,2)",
                oldNullable: true);
        }
    }
}
