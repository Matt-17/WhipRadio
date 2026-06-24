using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhipRadio.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class NewsPackageSegmentResume : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProducedSegmentsJson",
                table: "NewsPackages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StepIndex",
                table: "NewsPackages",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "StepTotal",
                table: "NewsPackages",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProducedSegmentsJson",
                table: "NewsPackages");

            migrationBuilder.DropColumn(
                name: "StepIndex",
                table: "NewsPackages");

            migrationBuilder.DropColumn(
                name: "StepTotal",
                table: "NewsPackages");
        }
    }
}
