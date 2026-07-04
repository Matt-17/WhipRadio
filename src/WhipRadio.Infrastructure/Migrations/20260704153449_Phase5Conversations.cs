using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhipRadio.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Phase5Conversations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DegradationReason",
                table: "ConversationSegments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReferencedTrackIdsJson",
                table: "ConversationSegments",
                type: "text",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DegradationReason",
                table: "ConversationSegments");

            migrationBuilder.DropColumn(
                name: "ReferencedTrackIdsJson",
                table: "ConversationSegments");
        }
    }
}
