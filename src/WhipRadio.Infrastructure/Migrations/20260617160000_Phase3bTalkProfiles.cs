using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using WhipRadio.Infrastructure.Persistence;

#nullable disable

namespace WhipRadio.Infrastructure.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(RadioDbContext))]
    [Migration("20260617160000_Phase3bTalkProfiles")]
    public partial class Phase3bTalkProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "TalkDensity",
                table: "Formats",
                type: "REAL",
                nullable: false,
                defaultValue: 0.5);

            migrationBuilder.AddColumn<string>(
                name: "TalkDepth",
                table: "Formats",
                type: "TEXT",
                nullable: false,
                defaultValue: "Light");

            migrationBuilder.Sql("""
                UPDATE Formats
                SET TalkDensity = Talkativeness;
                """);

            migrationBuilder.AddColumn<string>(
                name: "AllowedTalkPartKinds",
                table: "Moderators",
                type: "TEXT",
                nullable: false,
                defaultValue: "SongIntro,SongOutro,Banter,PersonalNote,Joke,ListenerGreeting,RequestDedication,StationId,Weather,HostChange");

            migrationBuilder.AddColumn<double>(
                name: "EvergreenBitTolerance",
                table: "Moderators",
                type: "REAL",
                nullable: false,
                defaultValue: 0.5);

            migrationBuilder.AddColumn<int>(
                name: "ExactReplayTolerance",
                table: "Moderators",
                type: "INTEGER",
                nullable: false,
                defaultValue: 2);

            migrationBuilder.AddColumn<int>(
                name: "MaxTalkPartsPerBreak",
                table: "Moderators",
                type: "INTEGER",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.AddColumn<int>(
                name: "MinTalkPartsPerBreak",
                table: "Moderators",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "TalkBreakFrequencyTracks",
                table: "Moderators",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TalkDensity",
                table: "Formats");

            migrationBuilder.DropColumn(
                name: "TalkDepth",
                table: "Formats");

            migrationBuilder.DropColumn(
                name: "AllowedTalkPartKinds",
                table: "Moderators");

            migrationBuilder.DropColumn(
                name: "EvergreenBitTolerance",
                table: "Moderators");

            migrationBuilder.DropColumn(
                name: "ExactReplayTolerance",
                table: "Moderators");

            migrationBuilder.DropColumn(
                name: "MaxTalkPartsPerBreak",
                table: "Moderators");

            migrationBuilder.DropColumn(
                name: "MinTalkPartsPerBreak",
                table: "Moderators");

            migrationBuilder.DropColumn(
                name: "TalkBreakFrequencyTracks",
                table: "Moderators");
        }
    }
}
