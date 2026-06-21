using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhipRadio.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ArtistMemberVoiceBootstrap : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TtsEngine",
                table: "ArtistMembers",
                type: "TEXT",
                nullable: false,
                defaultValue: "qwen");

            migrationBuilder.AddColumn<string>(
                name: "VoiceDesignLastError",
                table: "ArtistMembers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "VoiceDesignedAtUtc",
                table: "ArtistMembers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VoiceId",
                table: "ArtistMembers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VoiceReferencePath",
                table: "ArtistMembers",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TtsEngine",
                table: "ArtistMembers");

            migrationBuilder.DropColumn(
                name: "VoiceDesignLastError",
                table: "ArtistMembers");

            migrationBuilder.DropColumn(
                name: "VoiceDesignedAtUtc",
                table: "ArtistMembers");

            migrationBuilder.DropColumn(
                name: "VoiceId",
                table: "ArtistMembers");

            migrationBuilder.DropColumn(
                name: "VoiceReferencePath",
                table: "ArtistMembers");
        }
    }
}
