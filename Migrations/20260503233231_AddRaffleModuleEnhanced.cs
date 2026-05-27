using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntregasApi.Migrations
{
    /// <inheritdoc />
    public partial class AddRaffleModuleEnhanced : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "raffles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    image_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    social_share_image_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    animation_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    prize_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    prize_value = table.Column<decimal>(type: "numeric(12,2)", nullable: true),
                    prize_product_id = table.Column<Guid>(type: "uuid", nullable: true),
                    prize_description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    prize_currency = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    required_purchases = table.Column<int>(type: "integer", nullable: false),
                    eligibility_rule = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    min_order_total = table.Column<decimal>(type: "numeric(12,2)", nullable: true),
                    min_lifetime_spent = table.Column<decimal>(type: "numeric(12,2)", nullable: true),
                    date_range_start = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    date_range_end = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    max_entries_per_client = table.Column<int>(type: "integer", nullable: true),
                    client_segment_filter = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    new_clients_only = table.Column<bool>(type: "boolean", nullable: false),
                    frequent_clients_only = table.Column<bool>(type: "boolean", nullable: false),
                    vip_only = table.Column<bool>(type: "boolean", nullable: false),
                    exclude_blacklisted = table.Column<bool>(type: "boolean", nullable: false),
                    excluded_client_ids = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    tanda_id = table.Column<Guid>(type: "uuid", nullable: true),
                    shuffle_tanda_turns = table.Column<bool>(type: "boolean", nullable: false),
                    raffle_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    auto_draw = table.Column<bool>(type: "boolean", nullable: false),
                    notify_winner = table.Column<bool>(type: "boolean", nullable: false),
                    notification_channel = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    winner_message_template = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    social_template = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    social_bg_color = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    social_text_color = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    winner_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    winner_id = table.Column<int>(type: "integer", nullable: true),
                    announced_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_raffles", x => x.id);
                    table.ForeignKey(
                        name: "FK_raffles_Clients_winner_id",
                        column: x => x.winner_id,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_raffles_products_prize_product_id",
                        column: x => x.prize_product_id,
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_raffles_tandas_tanda_id",
                        column: x => x.tanda_id,
                        principalTable: "tandas",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "raffle_draws",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    raffle_id = table.Column<Guid>(type: "uuid", nullable: false),
                    draw_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    winner_id = table.Column<int>(type: "integer", nullable: true),
                    selection_method = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    is_tanda_shuffle = table.Column<bool>(type: "boolean", nullable: false),
                    tanda_turns_reshuffled = table.Column<int>(type: "integer", nullable: true),
                    notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_raffle_draws", x => x.id);
                    table.ForeignKey(
                        name: "FK_raffle_draws_Clients_winner_id",
                        column: x => x.winner_id,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_raffle_draws_raffles_raffle_id",
                        column: x => x.raffle_id,
                        principalTable: "raffles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "raffle_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    raffle_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<int>(type: "integer", nullable: false),
                    order_id = table.Column<int>(type: "integer", nullable: false),
                    entered_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_raffle_entries", x => x.id);
                    table.ForeignKey(
                        name: "FK_raffle_entries_Clients_client_id",
                        column: x => x.client_id,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_raffle_entries_Orders_order_id",
                        column: x => x.order_id,
                        principalTable: "Orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_raffle_entries_raffles_raffle_id",
                        column: x => x.raffle_id,
                        principalTable: "raffles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "raffle_participants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    raffle_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<int>(type: "integer", nullable: false),
                    qualification_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    qualifying_orders = table.Column<int>(type: "integer", nullable: false),
                    qualifying_total_spent = table.Column<decimal>(type: "numeric(12,2)", nullable: true),
                    entry_count = table.Column<int>(type: "integer", nullable: false),
                    is_winner = table.Column<bool>(type: "boolean", nullable: false),
                    assigned_tanda_turn = table.Column<int>(type: "integer", nullable: true),
                    previous_tanda_turn = table.Column<int>(type: "integer", nullable: true),
                    notified = table.Column<bool>(type: "boolean", nullable: false),
                    notified_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    notification_channel_used = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_raffle_participants", x => x.id);
                    table.ForeignKey(
                        name: "FK_raffle_participants_Clients_client_id",
                        column: x => x.client_id,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_raffle_participants_raffles_raffle_id",
                        column: x => x.raffle_id,
                        principalTable: "raffles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_raffle_draws_draw_date",
                table: "raffle_draws",
                column: "draw_date");

            migrationBuilder.CreateIndex(
                name: "IX_raffle_draws_raffle_id",
                table: "raffle_draws",
                column: "raffle_id");

            migrationBuilder.CreateIndex(
                name: "IX_raffle_draws_winner_id",
                table: "raffle_draws",
                column: "winner_id");

            migrationBuilder.CreateIndex(
                name: "IX_raffle_entries_client_id",
                table: "raffle_entries",
                column: "client_id");

            migrationBuilder.CreateIndex(
                name: "IX_raffle_entries_order_id",
                table: "raffle_entries",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "IX_raffle_entries_raffle_id_client_id",
                table: "raffle_entries",
                columns: new[] { "raffle_id", "client_id" });

            migrationBuilder.CreateIndex(
                name: "IX_raffle_participants_client_id",
                table: "raffle_participants",
                column: "client_id");

            migrationBuilder.CreateIndex(
                name: "IX_RaffleParticipant_Raffle_Client",
                table: "raffle_participants",
                columns: new[] { "raffle_id", "client_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_raffles_prize_product_id",
                table: "raffles",
                column: "prize_product_id");

            migrationBuilder.CreateIndex(
                name: "IX_raffles_raffle_date",
                table: "raffles",
                column: "raffle_date");

            migrationBuilder.CreateIndex(
                name: "IX_raffles_status",
                table: "raffles",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "IX_raffles_tanda_id",
                table: "raffles",
                column: "tanda_id");

            migrationBuilder.CreateIndex(
                name: "IX_raffles_winner_id",
                table: "raffles",
                column: "winner_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "raffle_draws");
            migrationBuilder.DropTable(name: "raffle_entries");
            migrationBuilder.DropTable(name: "raffle_participants");
            migrationBuilder.DropTable(name: "raffles");
        }
    }
}
