using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhipRadio.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Phase3aMixer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(name: "MixerEnabled", table: "StationSettings",
                type: "INTEGER", nullable: false, defaultValue: false);
            migrationBuilder.AddColumn<double>(name: "TargetLufs", table: "StationSettings",
                type: "REAL", nullable: false, defaultValue: -16.0);
            migrationBuilder.AddColumn<double>(name: "MaxMakeupGainDb", table: "StationSettings",
                type: "REAL", nullable: false, defaultValue: 6.0);
            migrationBuilder.AddColumn<double>(name: "DuckLevelDb", table: "StationSettings",
                type: "REAL", nullable: false, defaultValue: -12.0);
            migrationBuilder.AddColumn<int>(name: "DuckRampMs", table: "StationSettings",
                type: "INTEGER", nullable: false, defaultValue: 800);
            migrationBuilder.AddColumn<double>(name: "DefaultCrossfadeSeconds", table: "StationSettings",
                type: "REAL", nullable: false, defaultValue: 5.0);
            migrationBuilder.AddColumn<double>(name: "BeatAlignBpmTolerancePct", table: "StationSettings",
                type: "REAL", nullable: false, defaultValue: 5.0);
            migrationBuilder.AddColumn<int>(name: "HardCutGapAfterTalkMsMin", table: "StationSettings",
                type: "INTEGER", nullable: false, defaultValue: 200);
            migrationBuilder.AddColumn<int>(name: "HardCutGapAfterTalkMsMax", table: "StationSettings",
                type: "INTEGER", nullable: false, defaultValue: 600);
            migrationBuilder.AddColumn<int>(name: "HardCutGapSongMsMin", table: "StationSettings",
                type: "INTEGER", nullable: false, defaultValue: 0);
            migrationBuilder.AddColumn<int>(name: "HardCutGapSongMsMax", table: "StationSettings",
                type: "INTEGER", nullable: false, defaultValue: 150);
            migrationBuilder.AddColumn<int>(name: "PostHitSafetyMs", table: "StationSettings",
                type: "INTEGER", nullable: false, defaultValue: 800);
            migrationBuilder.AddColumn<string>(name: "StrategyWeightsJson", table: "StationSettings",
                type: "TEXT", nullable: false, defaultValue: "");
            migrationBuilder.AddColumn<bool>(name: "AnalysisRequired", table: "StationSettings",
                type: "INTEGER", nullable: false, defaultValue: false);

            migrationBuilder.CreateTable(
                name: "MediaAnalyses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ItemType = table.Column<string>(type: "TEXT", nullable: false),
                    ItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Bpm = table.Column<double>(type: "REAL", nullable: true),
                    BpmConfidence = table.Column<double>(type: "REAL", nullable: false),
                    BeatGridJson = table.Column<string>(type: "TEXT", nullable: true),
                    IntroEndSeconds = table.Column<double>(type: "REAL", nullable: true),
                    IntroConfidence = table.Column<double>(type: "REAL", nullable: false),
                    OutroStartSeconds = table.Column<double>(type: "REAL", nullable: true),
                    OutroConfidence = table.Column<double>(type: "REAL", nullable: false),
                    LeadingSilenceSeconds = table.Column<double>(type: "REAL", nullable: false),
                    TrailingSilenceSeconds = table.Column<double>(type: "REAL", nullable: false),
                    IntegratedLufs = table.Column<double>(type: "REAL", nullable: false),
                    TruePeakDb = table.Column<double>(type: "REAL", nullable: false),
                    EnergyProfileJson = table.Column<string>(type: "TEXT", nullable: false),
                    DurationSeconds = table.Column<double>(type: "REAL", nullable: false),
                    AnalyzerVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    AnalyzedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaAnalyses", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MediaAnalyses_ItemType_ItemId",
                table: "MediaAnalyses",
                columns: new[] { "ItemType", "ItemId" },
                unique: true);

            migrationBuilder.CreateTable(
                name: "TransitionLog",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OccurredAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    OutgoingType = table.Column<string>(type: "TEXT", nullable: false),
                    OutgoingId = table.Column<Guid>(type: "TEXT", nullable: false),
                    IncomingType = table.Column<string>(type: "TEXT", nullable: false),
                    IncomingId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Strategy = table.Column<string>(type: "TEXT", nullable: false),
                    OverlapSeconds = table.Column<double>(type: "REAL", nullable: false),
                    GapMs = table.Column<int>(type: "INTEGER", nullable: false),
                    ParametersJson = table.Column<string>(type: "TEXT", nullable: false),
                    ClipCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TransitionLog", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TransitionLog_OccurredAt",
                table: "TransitionLog",
                column: "OccurredAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "MediaAnalyses");
            migrationBuilder.DropTable(name: "TransitionLog");

            migrationBuilder.DropColumn(name: "MixerEnabled", table: "StationSettings");
            migrationBuilder.DropColumn(name: "TargetLufs", table: "StationSettings");
            migrationBuilder.DropColumn(name: "MaxMakeupGainDb", table: "StationSettings");
            migrationBuilder.DropColumn(name: "DuckLevelDb", table: "StationSettings");
            migrationBuilder.DropColumn(name: "DuckRampMs", table: "StationSettings");
            migrationBuilder.DropColumn(name: "DefaultCrossfadeSeconds", table: "StationSettings");
            migrationBuilder.DropColumn(name: "BeatAlignBpmTolerancePct", table: "StationSettings");
            migrationBuilder.DropColumn(name: "HardCutGapAfterTalkMsMin", table: "StationSettings");
            migrationBuilder.DropColumn(name: "HardCutGapAfterTalkMsMax", table: "StationSettings");
            migrationBuilder.DropColumn(name: "HardCutGapSongMsMin", table: "StationSettings");
            migrationBuilder.DropColumn(name: "HardCutGapSongMsMax", table: "StationSettings");
            migrationBuilder.DropColumn(name: "PostHitSafetyMs", table: "StationSettings");
            migrationBuilder.DropColumn(name: "StrategyWeightsJson", table: "StationSettings");
            migrationBuilder.DropColumn(name: "AnalysisRequired", table: "StationSettings");
        }
    }
}
