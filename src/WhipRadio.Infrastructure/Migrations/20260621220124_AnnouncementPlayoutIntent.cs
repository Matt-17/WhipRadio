using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhipRadio.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AnnouncementPlayoutIntent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PlayoutIntent",
                table: "Announcements",
                type: "TEXT",
                nullable: false,
                defaultValue: "Immediate");

            migrationBuilder.Sql("""
                UPDATE Announcements
                SET PlayoutIntent = 'ScheduledOnly'
                WHERE Id IN (
                    SELECT AnnouncementId
                    FROM NewsPackages
                    WHERE AnnouncementId IS NOT NULL
                )
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Announcements_PlayoutIntent",
                table: "Announcements",
                column: "PlayoutIntent");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Announcements_PlayoutIntent",
                table: "Announcements");

            migrationBuilder.DropColumn(
                name: "PlayoutIntent",
                table: "Announcements");
        }
    }
}
