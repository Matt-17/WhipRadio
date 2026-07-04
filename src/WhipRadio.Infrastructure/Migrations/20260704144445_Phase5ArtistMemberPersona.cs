using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhipRadio.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Phase5ArtistMemberPersona : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Age",
                table: "ArtistMembers",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Gender",
                table: "ArtistMembers",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Interests",
                table: "ArtistMembers",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Personality",
                table: "ArtistMembers",
                type: "text",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Age",
                table: "ArtistMembers");

            migrationBuilder.DropColumn(
                name: "Gender",
                table: "ArtistMembers");

            migrationBuilder.DropColumn(
                name: "Interests",
                table: "ArtistMembers");

            migrationBuilder.DropColumn(
                name: "Personality",
                table: "ArtistMembers");
        }
    }
}
