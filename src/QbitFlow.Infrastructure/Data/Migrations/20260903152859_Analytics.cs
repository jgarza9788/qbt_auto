using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QbitFlow.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Analytics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "LastWatchedUtc",
                table: "MediaItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "LegacyScore",
                table: "MediaItems",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "WeightedWatchTotal",
                table: "MediaItems",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastWatchedUtc",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "LegacyScore",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "WeightedWatchTotal",
                table: "MediaItems");
        }
    }
}
