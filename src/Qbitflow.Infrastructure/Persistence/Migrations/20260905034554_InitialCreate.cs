using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Qbitflow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AppSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ParallelismLevel = table.Column<int>(type: "INTEGER", nullable: false),
                    GlobalDryRun = table.Column<bool>(type: "INTEGER", nullable: false),
                    GlobalKillSwitch = table.Column<bool>(type: "INTEGER", nullable: false),
                    Theme = table.Column<string>(type: "TEXT", nullable: false),
                    LogLevel = table.Column<string>(type: "TEXT", nullable: false),
                    TimeZoneId = table.Column<string>(type: "TEXT", nullable: false),
                    SetupCompleted = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Instances",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    SourceType = table.Column<string>(type: "TEXT", nullable: false),
                    BaseUrl = table.Column<string>(type: "TEXT", nullable: false),
                    ApiKeyProtected = table.Column<string>(type: "TEXT", nullable: true),
                    Username = table.Column<string>(type: "TEXT", nullable: true),
                    PasswordProtected = table.Column<string>(type: "TEXT", nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    TimeoutSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    VerifySsl = table.Column<bool>(type: "INTEGER", nullable: false),
                    ExtraConfigJson = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Instances", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Rules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    StopOnMatch = table.Column<bool>(type: "INTEGER", nullable: false),
                    DryRun = table.Column<bool>(type: "INTEGER", nullable: false),
                    CronExpression = table.Column<string>(type: "TEXT", nullable: false),
                    TimeZoneId = table.Column<string>(type: "TEXT", nullable: false),
                    ConditionTreeJson = table.Column<string>(type: "TEXT", nullable: false),
                    AdvancedSqlWhere = table.Column<string>(type: "TEXT", nullable: true),
                    UseAdvancedSql = table.Column<bool>(type: "INTEGER", nullable: false),
                    ActionsJson = table.Column<string>(type: "TEXT", nullable: false),
                    TargetInstanceIdsJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Rules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RunRecords",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RuleId = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Outcome = table.Column<string>(type: "TEXT", nullable: false),
                    MatchedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ActionsExecutedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ActionsSkippedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ActionsFailedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    WasDryRun = table.Column<bool>(type: "INTEGER", nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    DetailsJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StoragePaths",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Path = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    FolderSizeScanIntervalMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StoragePaths", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "AppSettings",
                columns: new[] { "Id", "GlobalDryRun", "GlobalKillSwitch", "LogLevel", "ParallelismLevel", "SetupCompleted", "Theme", "TimeZoneId" },
                values: new object[] { 1, false, false, "Information", 1, false, "system", "UTC" });

            migrationBuilder.CreateIndex(
                name: "IX_Instances_Name",
                table: "Instances",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Rules_Priority",
                table: "Rules",
                column: "Priority");

            migrationBuilder.CreateIndex(
                name: "IX_RunRecords_RuleId",
                table: "RunRecords",
                column: "RuleId");

            migrationBuilder.CreateIndex(
                name: "IX_RunRecords_StartedAt",
                table: "RunRecords",
                column: "StartedAt");

            migrationBuilder.CreateIndex(
                name: "IX_StoragePaths_Name",
                table: "StoragePaths",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppSettings");

            migrationBuilder.DropTable(
                name: "Instances");

            migrationBuilder.DropTable(
                name: "Rules");

            migrationBuilder.DropTable(
                name: "RunRecords");

            migrationBuilder.DropTable(
                name: "StoragePaths");
        }
    }
}
