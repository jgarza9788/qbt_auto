using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Qbitflow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPathMappingRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PathMappingRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourcePrefix = table.Column<string>(type: "TEXT", nullable: false),
                    CanonicalPrefix = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PathMappingRules", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PathMappingRules");
        }
    }
}
