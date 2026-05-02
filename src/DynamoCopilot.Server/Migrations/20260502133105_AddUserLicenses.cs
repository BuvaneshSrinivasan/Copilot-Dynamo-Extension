using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DynamoCopilot.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddUserLicenses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LicenseEndDate",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "LicenseStartDate",
                table: "Users");

            migrationBuilder.CreateTable(
                name: "UserLicenses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Extension = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    StartDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserLicenses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserLicenses_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserLicenses_UserId_Extension",
                table: "UserLicenses",
                columns: new[] { "UserId", "Extension" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserLicenses");

            migrationBuilder.AddColumn<DateTime>(
                name: "LicenseEndDate",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LicenseStartDate",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);
        }
    }
}
