using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhipRadio.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RichArtistProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CreationHint",
                table: "Artists",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeepBackgroundBiography",
                table: "Artists",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FormationYear",
                table: "Artists",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GenerationPrompt",
                table: "Artists",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Origin",
                table: "Artists",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PromotionText",
                table: "Artists",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Type",
                table: "Artists",
                type: "TEXT",
                nullable: false,
                defaultValue: "Artist");

            migrationBuilder.CreateTable(
                name: "ArtistMembers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ArtistId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Role = table.Column<string>(type: "TEXT", nullable: false),
                    Biography = table.Column<string>(type: "TEXT", nullable: false),
                    VoiceCreationPrompt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArtistMembers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ArtistMembers_Artists_ArtistId",
                        column: x => x.ArtistId,
                        principalTable: "Artists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ArtistMembers_ArtistId_SortOrder",
                table: "ArtistMembers",
                columns: new[] { "ArtistId", "SortOrder" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ArtistMembers");

            migrationBuilder.DropColumn(
                name: "CreationHint",
                table: "Artists");

            migrationBuilder.DropColumn(
                name: "DeepBackgroundBiography",
                table: "Artists");

            migrationBuilder.DropColumn(
                name: "FormationYear",
                table: "Artists");

            migrationBuilder.DropColumn(
                name: "GenerationPrompt",
                table: "Artists");

            migrationBuilder.DropColumn(
                name: "Origin",
                table: "Artists");

            migrationBuilder.DropColumn(
                name: "PromotionText",
                table: "Artists");

            migrationBuilder.DropColumn(
                name: "Type",
                table: "Artists");
        }
    }
}
