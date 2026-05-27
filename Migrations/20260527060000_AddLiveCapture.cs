using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EntregasApi.Migrations
{
    /// <inheritdoc />
    public partial class AddLiveCapture : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
CREATE TABLE IF NOT EXISTS ""LiveSessions"" (
    ""Id"" serial PRIMARY KEY,
    ""FacebookUrl"" varchar(500) NOT NULL,
    ""Title"" varchar(200),
    ""R2Key"" varchar(500),
    ""Status"" int NOT NULL DEFAULT 0,
    ""StatusDetail"" varchar(500),
    ""ImportedAt"" timestamptz NOT NULL,
    ""ProcessedAt"" timestamptz,
    ""DurationSeconds"" float8
);
");

            migrationBuilder.Sql(@"
CREATE TABLE IF NOT EXISTS ""LiveProducts"" (
    ""Id"" serial PRIMARY KEY,
    ""LiveSessionId"" int NOT NULL REFERENCES ""LiveSessions""(""Id"") ON DELETE CASCADE,
    ""Keyword"" varchar(100) NOT NULL,
    ""Description"" varchar(300),
    ""Price"" numeric NOT NULL,
    ""AnnouncedAtSeconds"" float8
);
");

            migrationBuilder.Sql(@"
CREATE TABLE IF NOT EXISTS ""LiveSpokenOrders"" (
    ""Id"" serial PRIMARY KEY,
    ""LiveSessionId"" int NOT NULL REFERENCES ""LiveSessions""(""Id"") ON DELETE CASCADE,
    ""Keyword"" varchar(100) NOT NULL,
    ""ClientNameSpoken"" varchar(200) NOT NULL,
    ""SpokenAtSeconds"" float8
);
");

            migrationBuilder.Sql(@"
CREATE TABLE IF NOT EXISTS ""LiveCommentOrders"" (
    ""Id"" serial PRIMARY KEY,
    ""LiveSessionId"" int NOT NULL REFERENCES ""LiveSessions""(""Id"") ON DELETE CASCADE,
    ""Keyword"" varchar(100) NOT NULL,
    ""CommentDisplayName"" varchar(200) NOT NULL,
    ""CommentedAtSeconds"" float8,
    ""OcrConfidence"" float8 NOT NULL DEFAULT 0
);
");

            migrationBuilder.Sql(@"
CREATE TABLE IF NOT EXISTS ""LiveCandidates"" (
    ""Id"" serial PRIMARY KEY,
    ""LiveSessionId"" int NOT NULL REFERENCES ""LiveSessions""(""Id"") ON DELETE CASCADE,
    ""LiveProductId"" int REFERENCES ""LiveProducts""(""Id"") ON DELETE SET NULL,
    ""Keyword"" varchar(100) NOT NULL,
    ""ClientNameSpoken"" varchar(200),
    ""CommentDisplayName"" varchar(200),
    ""ResolvedClientId"" int REFERENCES ""Clients""(""Id"") ON DELETE SET NULL,
    ""ProposedAliasPairJson"" varchar(400),
    ""Source"" int NOT NULL DEFAULT 0,
    ""Status"" int NOT NULL DEFAULT 0,
    ""CreatedOrderId"" int
);
");

            migrationBuilder.Sql(@"
CREATE INDEX IF NOT EXISTS ""IX_LiveCandidates_SessionStatus""
ON ""LiveCandidates"" (""LiveSessionId"", ""Status"");
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_LiveCandidates_SessionStatus"";");
            migrationBuilder.Sql(@"DROP TABLE IF EXISTS ""LiveCandidates"";");
            migrationBuilder.Sql(@"DROP TABLE IF EXISTS ""LiveCommentOrders"";");
            migrationBuilder.Sql(@"DROP TABLE IF EXISTS ""LiveSpokenOrders"";");
            migrationBuilder.Sql(@"DROP TABLE IF EXISTS ""LiveProducts"";");
            migrationBuilder.Sql(@"DROP TABLE IF EXISTS ""LiveSessions"";");
        }
    }
}
