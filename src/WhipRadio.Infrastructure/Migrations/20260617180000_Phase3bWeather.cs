using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using WhipRadio.Infrastructure.Persistence;

#nullable disable

namespace WhipRadio.Infrastructure.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(RadioDbContext))]
    [Migration("20260617180000_Phase3bWeather")]
    public partial class Phase3bWeather : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsWeatherSpecialist",
                table: "Moderators",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "WeatherCadenceMinutes",
                table: "StationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 60);

            migrationBuilder.AddColumn<bool>(
                name: "WeatherEnabled",
                table: "StationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "WeatherFullHandoverEnabled",
                table: "StationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "WeatherSpecialistModeratorId",
                table: "StationSettings",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsWeatherSpecialist",
                table: "Moderators");

            migrationBuilder.DropColumn(
                name: "WeatherCadenceMinutes",
                table: "StationSettings");

            migrationBuilder.DropColumn(
                name: "WeatherEnabled",
                table: "StationSettings");

            migrationBuilder.DropColumn(
                name: "WeatherFullHandoverEnabled",
                table: "StationSettings");

            migrationBuilder.DropColumn(
                name: "WeatherSpecialistModeratorId",
                table: "StationSettings");
        }
    }
}
