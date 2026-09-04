using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QbitFlow.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AppSettings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppSettings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "MediaItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MatchKey = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    MediaType = table.Column<string>(type: "TEXT", nullable: false),
                    Year = table.Column<int>(type: "INTEGER", nullable: true),
                    Rating = table.Column<double>(type: "REAL", nullable: true),
                    Genres = table.Column<string>(type: "TEXT", nullable: false),
                    DurationMs = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdatedUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MediaScoreCache",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    QbtInstanceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TorrentHash = table.Column<string>(type: "TEXT", nullable: false),
                    MediaItemId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Category = table.Column<string>(type: "TEXT", nullable: false),
                    WatchTotal = table.Column<double>(type: "REAL", nullable: false),
                    HotColdScore = table.Column<double>(type: "REAL", nullable: false),
                    DaysSinceLastWatched = table.Column<double>(type: "REAL", nullable: true),
                    IsMediaMatched = table.Column<bool>(type: "INTEGER", nullable: false),
                    ComputedUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaScoreCache", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Pipelines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    DryRun = table.Column<bool>(type: "INTEGER", nullable: false),
                    ScheduleKind = table.Column<string>(type: "TEXT", nullable: false),
                    IntervalSeconds = table.Column<int>(type: "INTEGER", nullable: true),
                    CronExpression = table.Column<string>(type: "TEXT", nullable: true),
                    TimeZoneId = table.Column<string>(type: "TEXT", nullable: false),
                    MaxParallelism = table.Column<int>(type: "INTEGER", nullable: false),
                    StopOnFirstMatch = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastRunUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    NextRunUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    LastRunId = table.Column<Guid>(type: "TEXT", nullable: true),
                    IsRunning = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Pipelines", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RuleConditionGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RuleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ParentGroupId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Logic = table.Column<string>(type: "TEXT", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuleConditionGroups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RuleConditionGroups_RuleConditionGroups_ParentGroupId",
                        column: x => x.ParentGroupId,
                        principalTable: "RuleConditionGroups",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "RunLogEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Seq = table.Column<long>(type: "INTEGER", nullable: false),
                    TimestampUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    Level = table.Column<string>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false),
                    TorrentHash = table.Column<string>(type: "TEXT", nullable: true),
                    RuleId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunLogEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ScriptRunMarkers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RuleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TorrentHash = table.Column<string>(type: "TEXT", nullable: false),
                    RunDir = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScriptRunMarkers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SourceConnections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    BaseUrl = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    AuthMode = table.Column<string>(type: "TEXT", nullable: false),
                    Username = table.Column<string>(type: "TEXT", nullable: true),
                    SecretCiphertext = table.Column<byte[]>(type: "BLOB", nullable: true),
                    SecretNonce = table.Column<byte[]>(type: "BLOB", nullable: true),
                    OptionsJson = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "{}"),
                    HealthState = table.Column<string>(type: "TEXT", nullable: false),
                    LastCheckedUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    LatencyMs = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceConnections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MediaFilePaths",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MediaItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Path = table.Column<string>(type: "TEXT", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaFilePaths", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MediaFilePaths_MediaItems_MediaItemId",
                        column: x => x.MediaItemId,
                        principalTable: "MediaItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MediaSourceStats",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MediaItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceConnectionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PlayCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastPlayedUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    SourceRating = table.Column<double>(type: "REAL", nullable: true),
                    WindowCountsJson = table.Column<string>(type: "TEXT", nullable: true),
                    FetchedUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaSourceStats", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MediaSourceStats_MediaItems_MediaItemId",
                        column: x => x.MediaItemId,
                        principalTable: "MediaItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RunHistory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PipelineId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Trigger = table.Column<string>(type: "TEXT", nullable: false),
                    DryRun = table.Column<bool>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    StartedUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    FinishedUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    DurationMs = table.Column<long>(type: "INTEGER", nullable: true),
                    TorrentsEvaluated = table.Column<int>(type: "INTEGER", nullable: false),
                    RulesEvaluated = table.Column<int>(type: "INTEGER", nullable: false),
                    ActionsApplied = table.Column<int>(type: "INTEGER", nullable: false),
                    ActionsWouldApply = table.Column<int>(type: "INTEGER", nullable: false),
                    ActionsSkipped = table.Column<int>(type: "INTEGER", nullable: false),
                    ErrorCount = table.Column<int>(type: "INTEGER", nullable: false),
                    SummaryJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunHistory", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RunHistory_Pipelines_PipelineId",
                        column: x => x.PipelineId,
                        principalTable: "Pipelines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RuleConditions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    GroupId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Field = table.Column<string>(type: "TEXT", nullable: false),
                    Operator = table.Column<string>(type: "TEXT", nullable: false),
                    ValueKind = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuleConditions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RuleConditions_RuleConditionGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "RuleConditionGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Rules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PipelineId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    StopOnMatch = table.Column<bool>(type: "INTEGER", nullable: true),
                    ConditionMode = table.Column<string>(type: "TEXT", nullable: false),
                    RawExpression = table.Column<string>(type: "TEXT", nullable: true),
                    RootGroupId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CompiledExpression = table.Column<string>(type: "TEXT", nullable: false),
                    CompiledUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    CompileValid = table.Column<bool>(type: "INTEGER", nullable: false),
                    CompileError = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Rules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Rules_Pipelines_PipelineId",
                        column: x => x.PipelineId,
                        principalTable: "Pipelines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Rules_RuleConditionGroups_RootGroupId",
                        column: x => x.RootGroupId,
                        principalTable: "RuleConditionGroups",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "PipelineSources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PipelineId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceConnectionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Roles = table.Column<string>(type: "TEXT", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false)
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

            migrationBuilder.CreateTable(
                name: "RunRuleResults",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RuleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RuleName = table.Column<string>(type: "TEXT", nullable: false),
                    SuccessCount = table.Column<int>(type: "INTEGER", nullable: false),
                    FailureCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ErrorCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ActionsApplied = table.Column<int>(type: "INTEGER", nullable: false),
                    ActionsWouldApply = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunRuleResults", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RunRuleResults_RunHistory_RunId",
                        column: x => x.RunId,
                        principalTable: "RunHistory",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RuleActions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RuleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    ParamsJson = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "{}")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuleActions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RuleActions_Rules_RuleId",
                        column: x => x.RuleId,
                        principalTable: "Rules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MediaFilePaths_FileName",
                table: "MediaFilePaths",
                column: "FileName");

            migrationBuilder.CreateIndex(
                name: "IX_MediaFilePaths_MediaItemId",
                table: "MediaFilePaths",
                column: "MediaItemId");

            migrationBuilder.CreateIndex(
                name: "IX_MediaItems_MatchKey",
                table: "MediaItems",
                column: "MatchKey");

            migrationBuilder.CreateIndex(
                name: "IX_MediaScoreCache_QbtInstanceId_TorrentHash",
                table: "MediaScoreCache",
                columns: new[] { "QbtInstanceId", "TorrentHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MediaSourceStats_MediaItemId_SourceConnectionId",
                table: "MediaSourceStats",
                columns: new[] { "MediaItemId", "SourceConnectionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PipelineSources_PipelineId_SourceConnectionId_Roles",
                table: "PipelineSources",
                columns: new[] { "PipelineId", "SourceConnectionId", "Roles" });

            migrationBuilder.CreateIndex(
                name: "IX_PipelineSources_SourceConnectionId",
                table: "PipelineSources",
                column: "SourceConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_RuleActions_RuleId",
                table: "RuleActions",
                column: "RuleId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RuleConditionGroups_ParentGroupId",
                table: "RuleConditionGroups",
                column: "ParentGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_RuleConditions_GroupId",
                table: "RuleConditions",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_Rules_PipelineId_Order",
                table: "Rules",
                columns: new[] { "PipelineId", "Order" });

            migrationBuilder.CreateIndex(
                name: "IX_Rules_RootGroupId",
                table: "Rules",
                column: "RootGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_RunHistory_PipelineId_StartedUtc",
                table: "RunHistory",
                columns: new[] { "PipelineId", "StartedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_RunLogEntries_RunId_Seq",
                table: "RunLogEntries",
                columns: new[] { "RunId", "Seq" });

            migrationBuilder.CreateIndex(
                name: "IX_RunRuleResults_RunId",
                table: "RunRuleResults",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_ScriptRunMarkers_RuleId_TorrentHash",
                table: "ScriptRunMarkers",
                columns: new[] { "RuleId", "TorrentHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceConnections_Name",
                table: "SourceConnections",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppSettings");

            migrationBuilder.DropTable(
                name: "MediaFilePaths");

            migrationBuilder.DropTable(
                name: "MediaScoreCache");

            migrationBuilder.DropTable(
                name: "MediaSourceStats");

            migrationBuilder.DropTable(
                name: "PipelineSources");

            migrationBuilder.DropTable(
                name: "RuleActions");

            migrationBuilder.DropTable(
                name: "RuleConditions");

            migrationBuilder.DropTable(
                name: "RunLogEntries");

            migrationBuilder.DropTable(
                name: "RunRuleResults");

            migrationBuilder.DropTable(
                name: "ScriptRunMarkers");

            migrationBuilder.DropTable(
                name: "MediaItems");

            migrationBuilder.DropTable(
                name: "SourceConnections");

            migrationBuilder.DropTable(
                name: "Rules");

            migrationBuilder.DropTable(
                name: "RunHistory");

            migrationBuilder.DropTable(
                name: "RuleConditionGroups");

            migrationBuilder.DropTable(
                name: "Pipelines");
        }
    }
}
