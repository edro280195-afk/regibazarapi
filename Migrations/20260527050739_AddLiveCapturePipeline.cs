using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace EntregasApi.Migrations
{
    /// <inheritdoc />
    public partial class AddLiveCapturePipeline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LiveSessions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FacebookUrl = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    R2Key = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    ImportedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    DurationSeconds = table.Column<double>(type: "double precision", nullable: true),
                    ProcessedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LiveSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LiveComments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LiveSessionId = table.Column<int>(type: "integer", nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CommentText = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    TimestampSeconds = table.Column<double>(type: "double precision", nullable: false),
                    OcrConfidence = table.Column<double>(type: "double precision", nullable: false),
                    RawText = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LiveComments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LiveComments_LiveSessions_LiveSessionId",
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
                    Keyword = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    NormalizedKeyword = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Price = table.Column<decimal>(type: "numeric(10,2)", nullable: false),
                    AnnouncedAtSeconds = table.Column<double>(type: "double precision", nullable: false),
                    Confidence = table.Column<double>(type: "double precision", nullable: false)
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
                    Keyword = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    NormalizedKeyword = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    ClientNameSpoken = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SpokenAtSeconds = table.Column<double>(type: "double precision", nullable: false),
                    Confidence = table.Column<double>(type: "double precision", nullable: false)
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
                name: "LiveTranscriptSegments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LiveSessionId = table.Column<int>(type: "integer", nullable: false),
                    StartSeconds = table.Column<double>(type: "double precision", nullable: false),
                    EndSeconds = table.Column<double>(type: "double precision", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LiveTranscriptSegments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LiveTranscriptSegments_LiveSessions_LiveSessionId",
                        column: x => x.LiveSessionId,
                        principalTable: "LiveSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LiveCommentOrders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LiveSessionId = table.Column<int>(type: "integer", nullable: false),
                    LiveCommentId = table.Column<int>(type: "integer", nullable: false),
                    Keyword = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    NormalizedKeyword = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    CommentDisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CommentedAtSeconds = table.Column<double>(type: "double precision", nullable: false),
                    OcrConfidence = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LiveCommentOrders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LiveCommentOrders_LiveComments_LiveCommentId",
                        column: x => x.LiveCommentId,
                        principalTable: "LiveComments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LiveCommentOrders_LiveSessions_LiveSessionId",
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
                    Keyword = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    NormalizedKeyword = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    ClientNameSpoken = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CommentDisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ResolvedClientId = table.Column<int>(type: "integer", nullable: true),
                    ProposedAlias = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ProposedCanonicalName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Source = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Confidence = table.Column<double>(type: "double precision", nullable: false),
                    SpokenAtSeconds = table.Column<double>(type: "double precision", nullable: true),
                    CommentedAtSeconds = table.Column<double>(type: "double precision", nullable: true),
                    CreatedOrderId = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReviewedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LiveCandidates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LiveCandidates_Clients_ResolvedClientId",
                        column: x => x.ResolvedClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
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
                    table.ForeignKey(
                        name: "FK_LiveCandidates_Orders_CreatedOrderId",
                        column: x => x.CreatedOrderId,
                        principalTable: "Orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LiveCandidates_CreatedOrderId",
                table: "LiveCandidates",
                column: "CreatedOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_LiveCandidates_LiveProductId",
                table: "LiveCandidates",
                column: "LiveProductId");

            migrationBuilder.CreateIndex(
                name: "IX_LiveCandidates_LiveSessionId_NormalizedKeyword",
                table: "LiveCandidates",
                columns: new[] { "LiveSessionId", "NormalizedKeyword" });

            migrationBuilder.CreateIndex(
                name: "IX_LiveCandidates_LiveSessionId_Status",
                table: "LiveCandidates",
                columns: new[] { "LiveSessionId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_LiveCandidates_ResolvedClientId",
                table: "LiveCandidates",
                column: "ResolvedClientId");

            migrationBuilder.CreateIndex(
                name: "IX_LiveCommentOrders_CommentedAtSeconds",
                table: "LiveCommentOrders",
                column: "CommentedAtSeconds");

            migrationBuilder.CreateIndex(
                name: "IX_LiveCommentOrders_LiveCommentId",
                table: "LiveCommentOrders",
                column: "LiveCommentId");

            migrationBuilder.CreateIndex(
                name: "IX_LiveCommentOrders_LiveSessionId_NormalizedKeyword",
                table: "LiveCommentOrders",
                columns: new[] { "LiveSessionId", "NormalizedKeyword" });

            migrationBuilder.CreateIndex(
                name: "IX_LiveComments_LiveSessionId_TimestampSeconds",
                table: "LiveComments",
                columns: new[] { "LiveSessionId", "TimestampSeconds" });

            migrationBuilder.CreateIndex(
                name: "IX_LiveProducts_LiveSessionId_NormalizedKeyword",
                table: "LiveProducts",
                columns: new[] { "LiveSessionId", "NormalizedKeyword" });

            migrationBuilder.CreateIndex(
                name: "IX_LiveProducts_NormalizedKeyword",
                table: "LiveProducts",
                column: "NormalizedKeyword");

            migrationBuilder.CreateIndex(
                name: "IX_LiveSessions_ImportedAt",
                table: "LiveSessions",
                column: "ImportedAt");

            migrationBuilder.CreateIndex(
                name: "IX_LiveSessions_Status",
                table: "LiveSessions",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_LiveSpokenOrders_LiveSessionId_NormalizedKeyword",
                table: "LiveSpokenOrders",
                columns: new[] { "LiveSessionId", "NormalizedKeyword" });

            migrationBuilder.CreateIndex(
                name: "IX_LiveSpokenOrders_SpokenAtSeconds",
                table: "LiveSpokenOrders",
                column: "SpokenAtSeconds");

            migrationBuilder.CreateIndex(
                name: "IX_LiveTranscriptSegments_LiveSessionId_StartSeconds",
                table: "LiveTranscriptSegments",
                columns: new[] { "LiveSessionId", "StartSeconds" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LiveCandidates");

            migrationBuilder.DropTable(
                name: "LiveCommentOrders");

            migrationBuilder.DropTable(
                name: "LiveSpokenOrders");

            migrationBuilder.DropTable(
                name: "LiveTranscriptSegments");

            migrationBuilder.DropTable(
                name: "LiveProducts");

            migrationBuilder.DropTable(
                name: "LiveComments");

            migrationBuilder.DropTable(
                name: "LiveSessions");
        }
    }
}
