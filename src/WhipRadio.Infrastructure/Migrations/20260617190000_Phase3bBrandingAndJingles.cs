using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using WhipRadio.Infrastructure.Persistence;

#nullable disable

namespace WhipRadio.Infrastructure.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(RadioDbContext))]
    [Migration("20260617190000_Phase3bBrandingAndJingles")]
    public partial class Phase3bBrandingAndJingles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "JingleId",
                table: "TalkParts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StationMission",
                table: "StationSettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "Create a continuous local radio experience where music, talk, weather, and listener moments feel intentional.");

            migrationBuilder.AddColumn<string>(
                name: "StationSlogan",
                table: "StationSettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "Llamas whipped the radio's mix.");

            migrationBuilder.AddColumn<string>(
                name: "StationVision",
                table: "StationSettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "A living AI radio station with original music, distinct hosts, and a coherent on-air identity.");

            migrationBuilder.CreateTable(
                name: "Jingles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Label = table.Column<string>(type: "TEXT", nullable: false),
                    Prompt = table.Column<string>(type: "TEXT", nullable: false),
                    Style = table.Column<string>(type: "TEXT", nullable: false),
                    Language = table.Column<string>(type: "TEXT", nullable: false),
                    DurationSeconds = table.Column<double>(type: "REAL", nullable: false),
                    FilePath = table.Column<string>(type: "TEXT", nullable: false),
                    Backend = table.Column<string>(type: "TEXT", nullable: false),
                    ModelUsed = table.Column<string>(type: "TEXT", nullable: true),
                    SeedUsed = table.Column<string>(type: "TEXT", nullable: true),
                    TaskId = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastUsedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PlayCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Jingles", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Jingles_CreatedAtUtc",
                table: "Jingles",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Jingles_IsActive",
                table: "Jingles",
                column: "IsActive");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "Jingles");

            migrationBuilder.DropColumn(
                name: "JingleId",
                table: "TalkParts");

            migrationBuilder.DropColumn(
                name: "StationMission",
                table: "StationSettings");

            migrationBuilder.DropColumn(
                name: "StationSlogan",
                table: "StationSettings");

            migrationBuilder.DropColumn(
                name: "StationVision",
                table: "StationSettings");
        }
    }
}
