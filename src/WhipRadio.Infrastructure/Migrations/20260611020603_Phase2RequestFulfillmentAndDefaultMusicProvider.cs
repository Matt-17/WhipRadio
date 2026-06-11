using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhipRadio.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Phase2RequestFulfillmentAndDefaultMusicProvider : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DefaultMusicProvider",
                table: "StationSettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "musicgen");

            migrationBuilder.AddColumn<Guid>(
                name: "FulfilledByTrackId",
                table: "ListenerMessages",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DefaultMusicProvider",
                table: "StationSettings");

            migrationBuilder.DropColumn(
                name: "FulfilledByTrackId",
                table: "ListenerMessages");
        }
    }
}
