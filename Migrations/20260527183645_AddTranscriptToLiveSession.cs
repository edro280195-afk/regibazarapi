using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace EntregasApi.Migrations
{
    /// <inheritdoc />
    public partial class AddTranscriptToLiveSession : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Deliveries_Orders_OrderId",
                table: "Deliveries");

            migrationBuilder.DropIndex(
                name: "IX_Deliveries_OrderId",
                table: "Deliveries");

            migrationBuilder.CreateTable(
                name: "LiveSessions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FacebookUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    R2Key = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    StatusDetail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ImportedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ProcessedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DurationSeconds = table.Column<double>(type: "double precision", nullable: true),
                    LocalAudioPath = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Transcript = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LiveSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LiveCommentOrders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LiveSessionId = table.Column<int>(type: "integer", nullable: false),
                    Keyword = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CommentDisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CommentedAtSeconds = table.Column<double>(type: "double precision", nullable: true),
                    OcrConfidence = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LiveCommentOrders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LiveCommentOrders_LiveSessions_LiveSessionId",
                        column: x => x.LiveSessionId,
                        principalTable: "LiveSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LiveProducts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LiveSessionId = table.Column<int>(type: "integer", nullable: false),
                    Keyword = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Price = table.Column<decimal>(type: "numeric", nullable: false),
                    AnnouncedAtSeconds = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LiveProducts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LiveProducts_LiveSessions_LiveSessionId",
                        column: x => x.LiveSessionId,
                        principalTable: "LiveSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LiveSpokenOrders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LiveSessionId = table.Column<int>(type: "integer", nullable: false),
                    Keyword = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ClientNameSpoken = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SpokenAtSeconds = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LiveSpokenOrders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LiveSpokenOrders_LiveSessions_LiveSessionId",
                        column: x => x.LiveSessionId,
                        principalTable: "LiveSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LiveCandidates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LiveSessionId = table.Column<int>(type: "integer", nullable: false),
                    LiveProductId = table.Column<int>(type: "integer", nullable: true),
                    Keyword = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ClientNameSpoken = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CommentDisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ResolvedClientId = table.Column<int>(type: "integer", nullable: true),
                    ProposedAliasPairJson = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    Source = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CreatedOrderId = table.Column<int>(type: "integer", nullable: true),
                    SpokenAtSeconds = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LiveCandidates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LiveCandidates_Clients_ResolvedClientId",
                        column: x => x.ResolvedClientId,
                        principalTable: "Clients",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_LiveCandidates_LiveProducts_LiveProductId",
                        column: x => x.LiveProductId,
                        principalTable: "LiveProducts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_LiveCandidates_LiveSessions_LiveSessionId",
                        column: x => x.LiveSessionId,
                        principalTable: "LiveSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Deliveries_OrderId",
                table: "Deliveries",
                column: "OrderId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LiveCandidates_LiveProductId",
                table: "LiveCandidates",
                column: "LiveProductId");

            migrationBuilder.CreateIndex(
                name: "IX_LiveCandidates_LiveSessionId",
                table: "LiveCandidates",
                column: "LiveSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_LiveCandidates_ResolvedClientId",
                table: "LiveCandidates",
                column: "ResolvedClientId");

            migrationBuilder.CreateIndex(
                name: "IX_LiveCommentOrders_LiveSessionId",
                table: "LiveCommentOrders",
                column: "LiveSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_LiveProducts_LiveSessionId",
                table: "LiveProducts",
                column: "LiveSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_LiveSpokenOrders_LiveSessionId",
                table: "LiveSpokenOrders",
                column: "LiveSessionId");

            migrationBuilder.AddForeignKey(
                name: "FK_Deliveries_Orders_OrderId",
                table: "Deliveries",
                column: "OrderId",
                principalTable: "Orders",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Deliveries_Orders_OrderId",
                table: "Deliveries");

            migrationBuilder.DropTable(
                name: "LiveCandidates");

            migrationBuilder.DropTable(
                name: "LiveCommentOrders");

            migrationBuilder.DropTable(
                name: "LiveSpokenOrders");

            migrationBuilder.DropTable(
                name: "LiveProducts");

            migrationBuilder.DropTable(
                name: "LiveSessions");

            migrationBuilder.DropIndex(
                name: "IX_Deliveries_OrderId",
                table: "Deliveries");

            migrationBuilder.CreateIndex(
                name: "IX_Deliveries_OrderId",
                table: "Deliveries",
                column: "OrderId",
                unique: true,
                filter: "\"OrderId\" IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_Deliveries_Orders_OrderId",
                table: "Deliveries",
                column: "OrderId",
                principalTable: "Orders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
