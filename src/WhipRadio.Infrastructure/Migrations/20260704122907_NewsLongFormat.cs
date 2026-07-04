using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhipRadio.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class NewsLongFormat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "NewsLongFormatAirTimes",
                table: "StationSettings",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "NewsLongFormatDurationMinutes",
                table: "StationSettings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "NewsLongFormatEnabled",
                table: "StationSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "NewsShowFormatId",
                table: "StationSettings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "Jingles",
                type: "text",
                nullable: false,
                defaultValue: "StationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NewsLongFormatAirTimes",
                table: "StationSettings");

            migrationBuilder.DropColumn(
                name: "NewsLongFormatDurationMinutes",
                table: "StationSettings");

            migrationBuilder.DropColumn(
                name: "NewsLongFormatEnabled",
                table: "StationSettings");

            migrationBuilder.DropColumn(
                name: "NewsShowFormatId",
                table: "StationSettings");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "Jingles");
        }
    }
}
