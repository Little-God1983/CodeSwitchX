using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeSwitchX.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PricingRules",
                columns: table => new
                {
                    Model = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: true),
                    InputPerM = table.Column<double>(type: "REAL", nullable: false),
                    OutputPerM = table.Column<double>(type: "REAL", nullable: false),
                    CacheWritePerM = table.Column<double>(type: "REAL", nullable: false),
                    CacheReadPerM = table.Column<double>(type: "REAL", nullable: false),
                    ContextWindow = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PricingRules", x => x.Model);
                });

            migrationBuilder.CreateTable(
                name: "SessionEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ToolName = table.Column<string>(type: "TEXT", nullable: true),
                    At = table.Column<long>(type: "INTEGER", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SessionEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Sessions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Title = table.Column<string>(type: "TEXT", nullable: true),
                    TitleLocked = table.Column<bool>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastEventAt = table.Column<long>(type: "INTEGER", nullable: false),
                    StateSince = table.Column<long>(type: "INTEGER", nullable: false),
                    Cwd = table.Column<string>(type: "TEXT", nullable: true),
                    TranscriptPath = table.Column<string>(type: "TEXT", nullable: true),
                    Model = table.Column<string>(type: "TEXT", nullable: true),
                    LastToolName = table.Column<string>(type: "TEXT", nullable: true),
                    LastNotification = table.Column<string>(type: "TEXT", nullable: true),
                    Inferred = table.Column<bool>(type: "INTEGER", nullable: false),
                    ClaudePid = table.Column<int>(type: "INTEGER", nullable: true),
                    ContextInput = table.Column<long>(type: "INTEGER", nullable: false),
                    ContextOutput = table.Column<long>(type: "INTEGER", nullable: false),
                    ContextCacheWrite = table.Column<long>(type: "INTEGER", nullable: false),
                    ContextCacheRead = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ValueJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "Tracks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tracks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TranscriptCursors",
                columns: table => new
                {
                    Path = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    ByteOffset = table.Column<long>(type: "INTEGER", nullable: false),
                    LastWriteUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TranscriptCursors", x => x.Path);
                });

            migrationBuilder.CreateTable(
                name: "UsageBuckets",
                columns: table => new
                {
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Model = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    MinuteUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    Input = table.Column<long>(type: "INTEGER", nullable: false),
                    Output = table.Column<long>(type: "INTEGER", nullable: false),
                    CacheWrite = table.Column<long>(type: "INTEGER", nullable: false),
                    CacheRead = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UsageBuckets", x => new { x.SessionId, x.Model, x.MinuteUtc });
                });

            migrationBuilder.CreateTable(
                name: "Workspaces",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    RootPath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    WorkspaceFile = table.Column<string>(type: "TEXT", nullable: true),
                    TrackId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccentColor = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    HostMode = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    VsCodeProfile = table.Column<string>(type: "TEXT", nullable: true),
                    AutoStart = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Workspaces", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Workspaces_Tracks_TrackId",
                        column: x => x.TrackId,
                        principalTable: "Tracks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Worktrees",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Path = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    Branch = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Worktrees", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Worktrees_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SessionEvents_At",
                table: "SessionEvents",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_SessionEvents_SessionId_At",
                table: "SessionEvents",
                columns: new[] { "SessionId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_LastEventAt",
                table: "Sessions",
                column: "LastEventAt");

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_WorkspaceId",
                table: "Sessions",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_UsageBuckets_MinuteUtc",
                table: "UsageBuckets",
                column: "MinuteUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Workspaces_RootPath",
                table: "Workspaces",
                column: "RootPath",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Workspaces_TrackId",
                table: "Workspaces",
                column: "TrackId");

            migrationBuilder.CreateIndex(
                name: "IX_Worktrees_Path",
                table: "Worktrees",
                column: "Path");

            migrationBuilder.CreateIndex(
                name: "IX_Worktrees_WorkspaceId",
                table: "Worktrees",
                column: "WorkspaceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PricingRules");

            migrationBuilder.DropTable(
                name: "SessionEvents");

            migrationBuilder.DropTable(
                name: "Sessions");

            migrationBuilder.DropTable(
                name: "Settings");

            migrationBuilder.DropTable(
                name: "TranscriptCursors");

            migrationBuilder.DropTable(
                name: "UsageBuckets");

            migrationBuilder.DropTable(
                name: "Worktrees");

            migrationBuilder.DropTable(
                name: "Workspaces");

            migrationBuilder.DropTable(
                name: "Tracks");
        }
    }
}
