using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhipRadio.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ArtistSocialPosts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ArtistPosts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ArtistId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TrackId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Body = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArtistPosts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ArtistPosts_Artists_ArtistId",
                        column: x => x.ArtistId,
                        principalTable: "Artists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ArtistPosts_Tracks_TrackId",
                        column: x => x.TrackId,
                        principalTable: "Tracks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ArtistPosts_ArtistId",
                table: "ArtistPosts",
                column: "ArtistId");

            migrationBuilder.CreateIndex(
                name: "IX_ArtistPosts_CreatedAtUtc",
                table: "ArtistPosts",
                column: "CreatedAtUtc",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_ArtistPosts_TrackId",
                table: "ArtistPosts",
                column: "TrackId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ArtistPosts");
        }
    }
}
