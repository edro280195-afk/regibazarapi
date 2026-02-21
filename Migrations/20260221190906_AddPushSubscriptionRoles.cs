using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntregasApi.Migrations
{
    /// <inheritdoc />
    public partial class AddPushSubscriptionRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PushSubscriptions_Clients_ClientId",
                table: "PushSubscriptions");

            migrationBuilder.DropIndex(
                name: "IX_PushSubscriptions_ClientId",
                table: "PushSubscriptions");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "PushSubscriptions");

            migrationBuilder.AlterColumn<string>(
                name: "P256dh",
                table: "PushSubscriptions",
                type: "character varying(512)",
                maxLength: 512,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "Endpoint",
                table: "PushSubscriptions",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedAt",
                table: "PushSubscriptions",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "NOW()",
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone");

            migrationBuilder.AlterColumn<string>(
                name: "Auth",
                table: "PushSubscriptions",
                type: "character varying(512)",
                maxLength: 512,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<string>(
                name: "DriverRouteToken",
                table: "PushSubscriptions",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastUsedAt",
                table: "PushSubscriptions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Role",
                table: "PushSubscriptions",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "client");

            migrationBuilder.CreateIndex(
                name: "IX_PushSub_Role_ClientId",
                table: "PushSubscriptions",
                columns: new[] { "Role", "ClientId" });

            migrationBuilder.CreateIndex(
                name: "IX_PushSub_Role_DriverToken",
                table: "PushSubscriptions",
                columns: new[] { "Role", "DriverRouteToken" });

            migrationBuilder.CreateIndex(
                name: "IX_PushSubscriptions_Endpoint",
                table: "PushSubscriptions",
                column: "Endpoint",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PushSub_Role_ClientId",
                table: "PushSubscriptions");

            migrationBuilder.DropIndex(
                name: "IX_PushSub_Role_DriverToken",
                table: "PushSubscriptions");

            migrationBuilder.DropIndex(
                name: "IX_PushSubscriptions_Endpoint",
                table: "PushSubscriptions");

            migrationBuilder.DropColumn(
                name: "DriverRouteToken",
                table: "PushSubscriptions");

            migrationBuilder.DropColumn(
                name: "LastUsedAt",
                table: "PushSubscriptions");

            migrationBuilder.DropColumn(
                name: "Role",
                table: "PushSubscriptions");

            migrationBuilder.AlterColumn<string>(
                name: "P256dh",
                table: "PushSubscriptions",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(512)",
                oldMaxLength: 512);

            migrationBuilder.AlterColumn<string>(
                name: "Endpoint",
                table: "PushSubscriptions",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(2048)",
                oldMaxLength: 2048);

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedAt",
                table: "PushSubscriptions",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone",
                oldDefaultValueSql: "NOW()");

            migrationBuilder.AlterColumn<string>(
                name: "Auth",
                table: "PushSubscriptions",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(512)",
                oldMaxLength: 512);

            migrationBuilder.AddColumn<int>(
                name: "UserId",
                table: "PushSubscriptions",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PushSubscriptions_ClientId",
                table: "PushSubscriptions",
                column: "ClientId");

            migrationBuilder.AddForeignKey(
                name: "FK_PushSubscriptions_Clients_ClientId",
                table: "PushSubscriptions",
                column: "ClientId",
                principalTable: "Clients",
                principalColumn: "Id");
        }
    }
}
