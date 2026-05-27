using EntregasApi.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntregasApi.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(AppDbContext))]
    [Migration("20260527070000_AddCandidateClipSupport")]
    public partial class AddCandidateClipSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
ALTER TABLE ""LiveCandidates"" ADD COLUMN IF NOT EXISTS ""SpokenAtSeconds"" double precision NULL;
");

            migrationBuilder.Sql(@"
ALTER TABLE ""LiveSessions"" ADD COLUMN IF NOT EXISTS ""LocalAudioPath"" varchar(500) NULL;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"ALTER TABLE ""LiveCandidates"" DROP COLUMN IF EXISTS ""SpokenAtSeconds"";");
            migrationBuilder.Sql(@"ALTER TABLE ""LiveSessions"" DROP COLUMN IF EXISTS ""LocalAudioPath"";");
        }
    }
}
