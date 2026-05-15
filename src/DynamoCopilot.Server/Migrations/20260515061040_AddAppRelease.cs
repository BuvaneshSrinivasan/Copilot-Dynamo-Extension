using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DynamoCopilot.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddAppRelease : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AppReleases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    Version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    MinVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ReleaseNotes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    DllsUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    DllsSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    DbVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    DbUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    DbSizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    PublishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppReleases", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppReleases");
        }
    }
}
