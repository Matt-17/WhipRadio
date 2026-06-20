using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhipRadio.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Phase3cNewsProduction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "NewsEnabled",
                table: "StationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "NewsExtractionEnabled",
                table: "StationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "NewsPackageCadenceMinutes",
                table: "StationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 60);

            migrationBuilder.AddColumn<int>(
                name: "NewsPackageMaxDurationSeconds",
                table: "StationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 300);

            migrationBuilder.AddColumn<int>(
                name: "NewsPresenterModeratorId",
                table: "StationSettings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "TopOfHourFadeOutSeconds",
                table: "StationSettings",
                type: "REAL",
                nullable: false,
                defaultValue: 1.0);

            migrationBuilder.AddColumn<int>(
                name: "TopOfHourIntroGraceSeconds",
                table: "StationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 10);

            migrationBuilder.AddColumn<double>(
                name: "WeatherLatitude",
                table: "StationSettings",
                type: "REAL",
                nullable: false,
                defaultValue: 40.7128);

            migrationBuilder.AddColumn<string>(
                name: "WeatherLocationName",
                table: "StationSettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "New York, US");

            migrationBuilder.AddColumn<double>(
                name: "WeatherLongitude",
                table: "StationSettings",
                type: "REAL",
                nullable: false,
                defaultValue: -74.006);

            migrationBuilder.CreateTable(
                name: "NewsFeeds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Label = table.Column<string>(type: "TEXT", nullable: false),
                    Url = table.Column<string>(type: "TEXT", nullable: false),
                    Language = table.Column<string>(type: "TEXT", nullable: false),
                    Region = table.Column<string>(type: "TEXT", nullable: false),
                    Category = table.Column<string>(type: "TEXT", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsSeeded = table.Column<bool>(type: "INTEGER", nullable: false),
                    PollCadenceMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxItemsPerPoll = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastPolledAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NewsFeeds", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NewsPackages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    TargetUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    TargetDurationSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    AnnouncementId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ProducedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    QueuedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PlayedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FailureReason = table.Column<string>(type: "TEXT", nullable: true),
                    SourceSummary = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NewsPackages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NewsPackages_Announcements_AnnouncementId",
                        column: x => x.AnnouncementId,
                        principalTable: "Announcements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "NewsItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    FeedId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Url = table.Column<string>(type: "TEXT", nullable: false),
                    Summary = table.Column<string>(type: "TEXT", nullable: true),
                    ExtractedSummary = table.Column<string>(type: "TEXT", nullable: true),
                    PublishedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FirstSeenAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    SelectionReason = table.Column<string>(type: "TEXT", nullable: true),
                    ProducedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NewsItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NewsItems_NewsFeeds_FeedId",
                        column: x => x.FeedId,
                        principalTable: "NewsFeeds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NewsFeeds_IsEnabled_LastPolledAtUtc",
                table: "NewsFeeds",
                columns: new[] { "IsEnabled", "LastPolledAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_NewsFeeds_Url",
                table: "NewsFeeds",
                column: "Url",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NewsItems_ContentHash",
                table: "NewsItems",
                column: "ContentHash");

            migrationBuilder.CreateIndex(
                name: "IX_NewsItems_FeedId_Url",
                table: "NewsItems",
                columns: new[] { "FeedId", "Url" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NewsItems_Status_PublishedAtUtc_FirstSeenAtUtc",
                table: "NewsItems",
                columns: new[] { "Status", "PublishedAtUtc", "FirstSeenAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_NewsPackages_AnnouncementId",
                table: "NewsPackages",
                column: "AnnouncementId");

            migrationBuilder.CreateIndex(
                name: "IX_NewsPackages_Kind_TargetUtc",
                table: "NewsPackages",
                columns: new[] { "Kind", "TargetUtc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NewsPackages_Status_TargetUtc",
                table: "NewsPackages",
                columns: new[] { "Status", "TargetUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NewsItems");

            migrationBuilder.DropTable(
                name: "NewsPackages");

            migrationBuilder.DropTable(
                name: "NewsFeeds");

            migrationBuilder.DropColumn(
                name: "NewsEnabled",
                table: "StationSettings");

            migrationBuilder.DropColumn(
                name: "NewsExtractionEnabled",
                table: "StationSettings");

            migrationBuilder.DropColumn(
                name: "NewsPackageCadenceMinutes",
                table: "StationSettings");

            migrationBuilder.DropColumn(
                name: "NewsPackageMaxDurationSeconds",
                table: "StationSettings");

            migrationBuilder.DropColumn(
                name: "NewsPresenterModeratorId",
                table: "StationSettings");

            migrationBuilder.DropColumn(
                name: "TopOfHourFadeOutSeconds",
                table: "StationSettings");

            migrationBuilder.DropColumn(
                name: "TopOfHourIntroGraceSeconds",
                table: "StationSettings");

            migrationBuilder.DropColumn(
                name: "WeatherLatitude",
                table: "StationSettings");

            migrationBuilder.DropColumn(
                name: "WeatherLocationName",
                table: "StationSettings");

            migrationBuilder.DropColumn(
                name: "WeatherLongitude",
                table: "StationSettings");
        }
    }
}
