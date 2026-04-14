using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Pgvector;

#nullable disable

namespace DynamoCopilot.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddDynamoNodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:vector", ",,");

            migrationBuilder.CreateTable(
                name: "DynamoNodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    Name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Category = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    PackageName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    PackageDescription = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Keywords = table.Column<string[]>(type: "text[]", nullable: true),
                    InputPorts = table.Column<string[]>(type: "text[]", nullable: true),
                    OutputPorts = table.Column<string[]>(type: "text[]", nullable: true),
                    NodeType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Embedding = table.Column<Vector>(type: "vector(768)", nullable: true),
                    IndexedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DynamoNodes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DynamoNodes_Embedding",
                table: "DynamoNodes",
                column: "Embedding")
                .Annotation("Npgsql:IndexMethod", "ivfflat")
                .Annotation("Npgsql:IndexOperators", new[] { "vector_cosine_ops" })
                .Annotation("Npgsql:StorageParameter:lists", 100);

            migrationBuilder.CreateIndex(
                name: "IX_DynamoNodes_PackageName_Name",
                table: "DynamoNodes",
                columns: new[] { "PackageName", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DynamoNodes");

            migrationBuilder.AlterDatabase()
                .OldAnnotation("Npgsql:PostgresExtension:vector", ",,");
        }
    }
}
