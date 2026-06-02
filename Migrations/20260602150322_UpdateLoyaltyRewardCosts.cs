using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntregasApi.Migrations
{
    /// <inheritdoc />
    public partial class UpdateLoyaltyRewardCosts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE \"LoyaltyRewards\" SET \"PointsCost\" = 150 WHERE \"Name\" = 'Envío gratis' AND \"PointsCost\" = 120;");
            migrationBuilder.Sql("UPDATE \"LoyaltyRewards\" SET \"PointsCost\" = 200 WHERE \"Name\" = '$100 de descuento' AND \"PointsCost\" = 180;");
            migrationBuilder.Sql("UPDATE \"LoyaltyRewards\" SET \"PointsCost\" = 300 WHERE \"Name\" = 'Regalito sorpresa' AND \"PointsCost\" = 250;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
