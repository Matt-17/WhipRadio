using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhipRadio.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class FormatSelectionRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DefaultArtistLookbackTracks",
                table: "StationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "DefaultMaxArtistPlaysPerHour",
                table: "StationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<double>(
                name: "FatigueFactor",
                table: "StationSettings",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<int>(
                name: "RecentExclusionCount",
                table: "StationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "SelectionDiversityEnabled",
                table: "StationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "SelectionRules_ArtistLookbackTracks",
                table: "Formats",
                type: "INTEGER",
                nullable: false,
                defaultValue: 8);

            migrationBuilder.AddColumn<double>(
                name: "SelectionRules_BpmTolerancePct",
                table: "Formats",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SelectionRules_FeaturedArtistId",
                table: "Formats",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SelectionRules_MaxArtistPlaysPerHour",
                table: "Formats",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SelectionRules_Mode",
                table: "Formats",
                type: "TEXT",
                nullable: false,
                defaultValue: "StandardRotation");

            migrationBuilder.AddColumn<bool>(
                name: "SelectionRules_PreferHostGenres",
                table: "Formats",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "SelectionRules_SubgenreRotation",
                table: "Formats",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<double>(
                name: "SelectionRules_TargetBpm",
                table: "Formats",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SelectionRules_Theme",
                table: "Formats",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlayLog_ItemType_PlayedAt",
                table: "PlayLog",
                columns: new[] { "ItemType", "PlayedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PlayLog_ItemType_PlayedAt",
                table: "PlayLog");

            migrationBuilder.DropColumn(
                name: "DefaultArtistLookbackTracks",
                table: "StationSettings");

            migrationBuilder.DropColumn(
                name: "DefaultMaxArtistPlaysPerHour",
                table: "StationSettings");

            migrationBuilder.DropColumn(
                name: "FatigueFactor",
                table: "StationSettings");

            migrationBuilder.DropColumn(
                name: "RecentExclusionCount",
                table: "StationSettings");

            migrationBuilder.DropColumn(
                name: "SelectionDiversityEnabled",
                table: "StationSettings");

            migrationBuilder.DropColumn(
                name: "SelectionRules_ArtistLookbackTracks",
                table: "Formats");

            migrationBuilder.DropColumn(
                name: "SelectionRules_BpmTolerancePct",
                table: "Formats");

            migrationBuilder.DropColumn(
                name: "SelectionRules_FeaturedArtistId",
                table: "Formats");

            migrationBuilder.DropColumn(
                name: "SelectionRules_MaxArtistPlaysPerHour",
                table: "Formats");

            migrationBuilder.DropColumn(
                name: "SelectionRules_Mode",
                table: "Formats");

            migrationBuilder.DropColumn(
                name: "SelectionRules_PreferHostGenres",
                table: "Formats");

            migrationBuilder.DropColumn(
                name: "SelectionRules_SubgenreRotation",
                table: "Formats");

            migrationBuilder.DropColumn(
                name: "SelectionRules_TargetBpm",
                table: "Formats");

            migrationBuilder.DropColumn(
                name: "SelectionRules_Theme",
                table: "Formats");
        }
    }
}
