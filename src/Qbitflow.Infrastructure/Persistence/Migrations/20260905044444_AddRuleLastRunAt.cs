using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Qbitflow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRuleLastRunAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastRunAt",
                table: "Rules",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastRunAt",
                table: "Rules");
        }
    }
}
