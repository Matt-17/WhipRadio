using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhipRadio.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ArtistLanguage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Language",
                table: "Artists",
                type: "TEXT",
                nullable: false,
                defaultValue: "en");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Language",
                table: "Artists");
        }
    }
}
