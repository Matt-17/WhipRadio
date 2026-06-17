using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using WhipRadio.Infrastructure.Persistence;

#nullable disable

namespace WhipRadio.Infrastructure.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(RadioDbContext))]
    [Migration("20260617170000_Phase3bTalkBreaksAndBits")]
    public partial class Phase3bTalkBreaksAndBits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TalkBits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ModeratorId = table.Column<int>(type: "INTEGER", nullable: false),
                    Premise = table.Column<string>(type: "TEXT", nullable: false),
                    Tags = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    CooldownDays = table.Column<int>(type: "INTEGER", nullable: false),
                    PlayCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ExactReplayCount = table.Column<int>(type: "INTEGER", nullable: false),
                    FreshRetellCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastUsedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RetiredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RetirementReason = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TalkBits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TalkBreaks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AnnouncementId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ModeratorId = table.Column<int>(type: "INTEGER", nullable: false),
                    Priority = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Purpose = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    TargetWindowStartUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    TargetWindowEndUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RenderedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PlayedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DurationSeconds = table.Column<double>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TalkBreaks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TalkBitRenditions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TalkBitId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: false),
                    FilePath = table.Column<string>(type: "TEXT", nullable: true),
                    DurationSeconds = table.Column<double>(type: "REAL", nullable: false),
                    CreatedFromRetelling = table.Column<bool>(type: "INTEGER", nullable: false),
                    PlayCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastPlayedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TalkBitRenditions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TalkBitRenditions_TalkBits_TalkBitId",
                        column: x => x.TalkBitId,
                        principalTable: "TalkBits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TalkParts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TalkBreakId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Priority = table.Column<string>(type: "TEXT", nullable: false),
                    Purpose = table.Column<string>(type: "TEXT", nullable: false),
                    AnnouncementId = table.Column<Guid>(type: "TEXT", nullable: true),
                    RelatedTrackId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TalkBitId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DesiredDurationSeconds = table.Column<int>(type: "INTEGER", nullable: true),
                    WordBudget = table.Column<int>(type: "INTEGER", nullable: true),
                    TargetWindowStartUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    TargetWindowEndUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TalkParts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TalkParts_TalkBreaks_TalkBreakId",
                        column: x => x.TalkBreakId,
                        principalTable: "TalkBreaks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TalkBitRenditions_TalkBitId",
                table: "TalkBitRenditions",
                column: "TalkBitId");

            migrationBuilder.CreateIndex(
                name: "IX_TalkBits_LastUsedAtUtc",
                table: "TalkBits",
                column: "LastUsedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_TalkBits_ModeratorId_Status",
                table: "TalkBits",
                columns: new[] { "ModeratorId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_TalkBreaks_AnnouncementId",
                table: "TalkBreaks",
                column: "AnnouncementId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TalkBreaks_Status_ExpiresAtUtc",
                table: "TalkBreaks",
                columns: new[] { "Status", "ExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_TalkParts_Status_ExpiresAtUtc",
                table: "TalkParts",
                columns: new[] { "Status", "ExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_TalkParts_TalkBreakId_SortOrder",
                table: "TalkParts",
                columns: new[] { "TalkBreakId", "SortOrder" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "TalkBitRenditions");
            migrationBuilder.DropTable(name: "TalkParts");
            migrationBuilder.DropTable(name: "TalkBits");
            migrationBuilder.DropTable(name: "TalkBreaks");
        }
    }
}
