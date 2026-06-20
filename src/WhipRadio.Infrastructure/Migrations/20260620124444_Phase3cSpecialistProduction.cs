using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhipRadio.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Phase3cSpecialistProduction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "NewsCategoryOrder",
                table: "StationSettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "general,business,technology,culture,regional");

            migrationBuilder.AddColumn<bool>(
                name: "IsNewsSpecialist",
                table: "Moderators",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NewsCategoryOrder",
                table: "StationSettings");

            migrationBuilder.DropColumn(
                name: "IsNewsSpecialist",
                table: "Moderators");
        }
    }
}
