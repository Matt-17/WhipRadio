using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhipRadio.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Moderators",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Language = table.Column<string>(type: "TEXT", nullable: false),
                    VoiceId = table.Column<string>(type: "TEXT", nullable: false),
                    SpeechRate = table.Column<double>(type: "REAL", nullable: false),
                    PersonaPrompt = table.Column<string>(type: "TEXT", nullable: false),
                    Style = table.Column<string>(type: "TEXT", nullable: false),
                    PrefersVocals = table.Column<bool>(type: "INTEGER", nullable: true),
                    PreferredGenres = table.Column<string>(type: "TEXT", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Moderators", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlayLog",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PlayedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ItemType = table.Column<string>(type: "TEXT", nullable: false),
                    ItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ModeratorId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StationSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StationName = table.Column<string>(type: "TEXT", nullable: false),
                    DefaultLanguage = table.Column<string>(type: "TEXT", nullable: false),
                    TargetQueueLength = table.Column<int>(type: "INTEGER", nullable: false),
                    AnnouncementEveryNTracks = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StationSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tracks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Genre = table.Column<string>(type: "TEXT", nullable: false),
                    Style = table.Column<string>(type: "TEXT", nullable: false),
                    HasVocals = table.Column<bool>(type: "INTEGER", nullable: false),
                    Lyrics = table.Column<string>(type: "TEXT", nullable: true),
                    DurationSeconds = table.Column<double>(type: "REAL", nullable: false),
                    FilePath = table.Column<string>(type: "TEXT", nullable: false),
                    GenerationPrompt = table.Column<string>(type: "TEXT", nullable: false),
                    Backend = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    PlayCount = table.Column<int>(type: "INTEGER", nullable: false),
                    UpVotes = table.Column<int>(type: "INTEGER", nullable: false),
                    DownVotes = table.Column<int>(type: "INTEGER", nullable: false),
                    IsRetired = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tracks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Announcements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ModeratorId = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    ScriptText = table.Column<string>(type: "TEXT", nullable: false),
                    VoicedText = table.Column<string>(type: "TEXT", nullable: false),
                    FilePath = table.Column<string>(type: "TEXT", nullable: false),
                    DurationSeconds = table.Column<double>(type: "REAL", nullable: false),
                    RelatedTrackId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    WasPlayed = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Announcements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Announcements_Moderators_ModeratorId",
                        column: x => x.ModeratorId,
                        principalTable: "Moderators",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ScheduleSlots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    HourOfDay = table.Column<int>(type: "INTEGER", nullable: false),
                    Genre = table.Column<string>(type: "TEXT", nullable: false),
                    ModeratorId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduleSlots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScheduleSlots_Moderators_ModeratorId",
                        column: x => x.ModeratorId,
                        principalTable: "Moderators",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Votes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TrackId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Direction = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ClientHint = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Votes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Votes_Tracks_TrackId",
                        column: x => x.TrackId,
                        principalTable: "Tracks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Announcements_ModeratorId",
                table: "Announcements",
                column: "ModeratorId");

            migrationBuilder.CreateIndex(
                name: "IX_Announcements_WasPlayed",
                table: "Announcements",
                column: "WasPlayed");

            migrationBuilder.CreateIndex(
                name: "IX_PlayLog_PlayedAt",
                table: "PlayLog",
                column: "PlayedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleSlots_HourOfDay",
                table: "ScheduleSlots",
                column: "HourOfDay",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleSlots_ModeratorId",
                table: "ScheduleSlots",
                column: "ModeratorId");

            migrationBuilder.CreateIndex(
                name: "IX_Tracks_Genre",
                table: "Tracks",
                column: "Genre");

            migrationBuilder.CreateIndex(
                name: "IX_Tracks_IsRetired",
                table: "Tracks",
                column: "IsRetired");

            migrationBuilder.CreateIndex(
                name: "IX_Votes_TrackId",
                table: "Votes",
                column: "TrackId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Announcements");

            migrationBuilder.DropTable(
                name: "PlayLog");

            migrationBuilder.DropTable(
                name: "ScheduleSlots");

            migrationBuilder.DropTable(
                name: "StationSettings");

            migrationBuilder.DropTable(
                name: "Votes");

            migrationBuilder.DropTable(
                name: "Moderators");

            migrationBuilder.DropTable(
                name: "Tracks");
        }
    }
}
