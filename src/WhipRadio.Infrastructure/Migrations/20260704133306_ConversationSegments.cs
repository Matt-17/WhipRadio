using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhipRadio.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ConversationSegments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PodcastShows",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Brief = table.Column<string>(type: "text", nullable: false),
                    EpisodeMinutes = table.Column<int>(type: "integer", nullable: false),
                    DayOfWeek = table.Column<int>(type: "integer", nullable: false),
                    StartMinute = table.Column<int>(type: "integer", nullable: false),
                    SlotDurationMinutes = table.Column<int>(type: "integer", nullable: false),
                    ParticipantsJson = table.Column<string>(type: "text", nullable: false),
                    FormatId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PodcastShows", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PodcastShows_Formats_FormatId",
                        column: x => x.FormatId,
                        principalTable: "Formats",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ConversationSegments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    Structure = table.Column<string>(type: "text", nullable: false),
                    Topic = table.Column<string>(type: "text", nullable: false),
                    Brief = table.Column<string>(type: "text", nullable: false),
                    TargetDurationMinutes = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    ParticipantsJson = table.Column<string>(type: "text", nullable: false),
                    ChaptersJson = table.Column<string>(type: "text", nullable: false),
                    TurnsJson = table.Column<string>(type: "text", nullable: true),
                    Title = table.Column<string>(type: "text", nullable: true),
                    Transcript = table.Column<string>(type: "text", nullable: true),
                    OutputFilePath = table.Column<string>(type: "text", nullable: true),
                    DurationSeconds = table.Column<double>(type: "double precision", nullable: false),
                    AnnouncementId = table.Column<Guid>(type: "uuid", nullable: true),
                    PodcastShowId = table.Column<Guid>(type: "uuid", nullable: true),
                    TargetUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FailureReason = table.Column<string>(type: "text", nullable: true),
                    ProductionState = table.Column<string>(type: "text", nullable: true),
                    StepIndex = table.Column<int>(type: "integer", nullable: false),
                    StepTotal = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ProducedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UsedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationSegments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConversationSegments_Announcements_AnnouncementId",
                        column: x => x.AnnouncementId,
                        principalTable: "Announcements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ConversationSegments_PodcastShows_PodcastShowId",
                        column: x => x.PodcastShowId,
                        principalTable: "PodcastShows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConversationSegments_AnnouncementId",
                table: "ConversationSegments",
                column: "AnnouncementId");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationSegments_PodcastShowId_TargetUtc",
                table: "ConversationSegments",
                columns: new[] { "PodcastShowId", "TargetUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ConversationSegments_Status",
                table: "ConversationSegments",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationSegments_TargetUtc",
                table: "ConversationSegments",
                column: "TargetUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PodcastShows_FormatId",
                table: "PodcastShows",
                column: "FormatId");

            migrationBuilder.CreateIndex(
                name: "IX_PodcastShows_IsEnabled",
                table: "PodcastShows",
                column: "IsEnabled");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConversationSegments");

            migrationBuilder.DropTable(
                name: "PodcastShows");
        }
    }
}
