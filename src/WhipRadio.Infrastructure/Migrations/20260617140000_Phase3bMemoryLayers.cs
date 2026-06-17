using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using WhipRadio.Infrastructure.Persistence;

#nullable disable

namespace WhipRadio.Infrastructure.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(RadioDbContext))]
    [Migration("20260617140000_Phase3bMemoryLayers")]
    public partial class Phase3bMemoryLayers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ModeratorMemories_ModeratorId_Date",
                table: "ModeratorMemories");

            migrationBuilder.AddColumn<string>(
                name: "Layer",
                table: "ModeratorMemories",
                type: "TEXT",
                nullable: false,
                defaultValue: "DayMemory");

            migrationBuilder.CreateIndex(
                name: "IX_ModeratorMemories_ModeratorId_Layer_Date",
                table: "ModeratorMemories",
                columns: new[] { "ModeratorId", "Layer", "Date" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ModeratorMemories_ModeratorId_Layer_Date",
                table: "ModeratorMemories");

            migrationBuilder.DropColumn(
                name: "Layer",
                table: "ModeratorMemories");

            migrationBuilder.CreateIndex(
                name: "IX_ModeratorMemories_ModeratorId_Date",
                table: "ModeratorMemories",
                columns: new[] { "ModeratorId", "Date" });
        }
    }
}
