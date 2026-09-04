using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QbitFlow.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class FlattenModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Rules_Pipelines_PipelineId",
                table: "Rules");

            migrationBuilder.DropForeignKey(
                name: "FK_RunHistory_Pipelines_PipelineId",
                table: "RunHistory");

            migrationBuilder.DropTable(
                name: "PipelineSources");

            migrationBuilder.DropTable(
                name: "Pipelines");

            migrationBuilder.DropIndex(
                name: "IX_RunHistory_PipelineId_StartedUtc",
                table: "RunHistory");

            migrationBuilder.DropIndex(
                name: "IX_Rules_PipelineId_Order",
                table: "Rules");

            migrationBuilder.DropColumn(
                name: "PipelineId",
                table: "RunHistory");

            migrationBuilder.DropColumn(
                name: "PipelineId",
                table: "Rules");

            migrationBuilder.RenameColumn(
                name: "HotColdScore",
                table: "MediaScoreCache",
                newName: "WatchPopularity");

            migrationBuilder.AddColumn<int>(
                name: "CooldownSeconds",
                table: "Rules",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetFilterJson",
                table: "Rules",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_RunHistory_StartedUtc",
                table: "RunHistory",
                column: "StartedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Rules_Order",
                table: "Rules",
                column: "Order");

            // Rules from every former pipeline now share one list. Their `Order` values are kept
            // as-is, so a former multi-pipeline install may see duplicate positions until the user
            // drags them into the order they want — a one-time cosmetic quirk, not a correctness one.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RunHistory_StartedUtc",
                table: "RunHistory");

            migrationBuilder.DropIndex(
                name: "IX_Rules_Order",
                table: "Rules");

            migrationBuilder.DropColumn(
                name: "CooldownSeconds",
                table: "Rules");

            migrationBuilder.DropColumn(
                name: "TargetFilterJson",
                table: "Rules");

            migrationBuilder.RenameColumn(
                name: "WatchPopularity",
                table: "MediaScoreCache",
                newName: "HotColdScore");

            migrationBuilder.AddColumn<Guid>(
                name: "PipelineId",
                table: "RunHistory",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "PipelineId",
                table: "Rules",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "Pipelines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    CronExpression = table.Column<string>(type: "TEXT", nullable: true),
                    DryRun = table.Column<bool>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    IntervalSeconds = table.Column<int>(type: "INTEGER", nullable: true),
                    IsRunning = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastRunId = table.Column<Guid>(type: "TEXT", nullable: true),
                    LastRunUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    MaxParallelism = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    NextRunUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    ScheduleKind = table.Column<string>(type: "TEXT", nullable: false),
                    StopOnFirstMatch = table.Column<bool>(type: "INTEGER", nullable: false),
                    TimeZoneId = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Pipelines", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PipelineSources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PipelineId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceConnectionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    Roles = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PipelineSources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PipelineSources_Pipelines_PipelineId",
                        column: x => x.PipelineId,
                        principalTable: "Pipelines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PipelineSources_SourceConnections_SourceConnectionId",
                        column: x => x.SourceConnectionId,
                        principalTable: "SourceConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RunHistory_PipelineId_StartedUtc",
                table: "RunHistory",
                columns: new[] { "PipelineId", "StartedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Rules_PipelineId_Order",
                table: "Rules",
                columns: new[] { "PipelineId", "Order" });

            migrationBuilder.CreateIndex(
                name: "IX_PipelineSources_PipelineId_SourceConnectionId_Roles",
                table: "PipelineSources",
                columns: new[] { "PipelineId", "SourceConnectionId", "Roles" });

            migrationBuilder.CreateIndex(
                name: "IX_PipelineSources_SourceConnectionId",
                table: "PipelineSources",
                column: "SourceConnectionId");

            migrationBuilder.AddForeignKey(
                name: "FK_Rules_Pipelines_PipelineId",
                table: "Rules",
                column: "PipelineId",
                principalTable: "Pipelines",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_RunHistory_Pipelines_PipelineId",
                table: "RunHistory",
                column: "PipelineId",
                principalTable: "Pipelines",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
