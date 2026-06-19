using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhipRadio.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class TrackSongPlan : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Language",
                table: "Tracks",
                type: "TEXT",
                nullable: false,
                defaultValue: "en");

            migrationBuilder.AddColumn<string>(
                name: "SongStory",
                table: "Tracks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TargetDurationSeconds",
                table: "Tracks",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Language",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "SongStory",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "TargetDurationSeconds",
                table: "Tracks");
        }
    }
}
