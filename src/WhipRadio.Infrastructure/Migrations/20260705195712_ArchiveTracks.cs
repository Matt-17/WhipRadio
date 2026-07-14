using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhipRadio.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ArchiveTracks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FileHash",
                table: "Tracks",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "FileMissing",
                table: "Tracks",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "FileModifiedUtc",
                table: "Tracks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "FileSizeBytes",
                table: "Tracks",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImportedAlbum",
                table: "Tracks",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImportedArtist",
                table: "Tracks",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ImportedYear",
                table: "Tracks",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastEnrichmentAttemptUtc",
                table: "Tracks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "MetadataConfidence",
                table: "Tracks",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MetadataStatus",
                table: "Tracks",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Source",
                table: "Tracks",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "ArchiveEnrichmentEnabled",
                table: "StationSettings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "ArchivePlayoutEnabled",
                table: "StationSettings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "ArchiveUploadEnabled",
                table: "StationSettings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "PodcastKnowledgeEnabled",
                table: "StationSettings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tracks_FileHash",
                table: "Tracks",
                column: "FileHash");

            migrationBuilder.CreateIndex(
                name: "IX_Tracks_Source",
                table: "Tracks",
                column: "Source");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Tracks_FileHash",
                table: "Tracks");

            migrationBuilder.DropIndex(
                name: "IX_Tracks_Source",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "FileHash",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "FileMissing",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "FileModifiedUtc",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "FileSizeBytes",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "ImportedAlbum",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "ImportedArtist",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "ImportedYear",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "LastEnrichmentAttemptUtc",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "MetadataConfidence",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "MetadataStatus",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "ArchiveEnrichmentEnabled",
                table: "StationSettings");

            migrationBuilder.DropColumn(
                name: "ArchivePlayoutEnabled",
                table: "StationSettings");

            migrationBuilder.DropColumn(
                name: "ArchiveUploadEnabled",
                table: "StationSettings");

            migrationBuilder.DropColumn(
                name: "PodcastKnowledgeEnabled",
                table: "StationSettings");
        }
    }
}
