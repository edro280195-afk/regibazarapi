using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntregasApi.Migrations
{
    /// <inheritdoc />
    public partial class AddClientMergeAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                CREATE TABLE ""ClientMergeAudits"" (
                    ""Id"" SERIAL PRIMARY KEY,
                    ""SourceClientId"" int NOT NULL,
                    ""SourceName"" varchar(200) NOT NULL,
                    ""TargetClientId"" int NOT NULL,
                    ""TargetName"" varchar(200) NOT NULL,
                    ""Mode"" int NOT NULL DEFAULT 0,
                    ""Reason"" varchar(300),
                    ""Confidence"" double precision NOT NULL DEFAULT 0,
                    ""OrdersMoved"" int NOT NULL DEFAULT 0,
                    ""AliasesMoved"" int NOT NULL DEFAULT 0,
                    ""MergedAt"" timestamptz NOT NULL DEFAULT now()
                );
                CREATE INDEX ""IX_ClientMergeAudits_MergedAt"" ON ""ClientMergeAudits"" (""MergedAt"" DESC);
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP TABLE IF EXISTS ""ClientMergeAudits"";");
        }
    }
}
