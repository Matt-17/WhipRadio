using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhipRadio.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Phase5GroupChat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SenderArtistMemberId",
                table: "ChatMessages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SenderGuestId",
                table: "ChatMessages",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ChatChannelMembers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ChannelId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    ModeratorId = table.Column<int>(type: "integer", nullable: true),
                    ArtistMemberId = table.Column<Guid>(type: "uuid", nullable: true),
                    GuestId = table.Column<Guid>(type: "uuid", nullable: true),
                    DisplayName = table.Column<string>(type: "text", nullable: false),
                    JoinedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatChannelMembers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChatChannelMembers_ArtistMembers_ArtistMemberId",
                        column: x => x.ArtistMemberId,
                        principalTable: "ArtistMembers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ChatChannelMembers_ChatChannels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "ChatChannels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ChatChannelMembers_Guests_GuestId",
                        column: x => x.GuestId,
                        principalTable: "Guests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ChatChannelMembers_Moderators_ModeratorId",
                        column: x => x.ModeratorId,
                        principalTable: "Moderators",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChatMessages_SenderArtistMemberId",
                table: "ChatMessages",
                column: "SenderArtistMemberId");

            migrationBuilder.CreateIndex(
                name: "IX_ChatMessages_SenderGuestId",
                table: "ChatMessages",
                column: "SenderGuestId");

            migrationBuilder.CreateIndex(
                name: "IX_ChatChannelMembers_ArtistMemberId",
                table: "ChatChannelMembers",
                column: "ArtistMemberId");

            migrationBuilder.CreateIndex(
                name: "IX_ChatChannelMembers_ChannelId",
                table: "ChatChannelMembers",
                column: "ChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_ChatChannelMembers_GuestId",
                table: "ChatChannelMembers",
                column: "GuestId");

            migrationBuilder.CreateIndex(
                name: "IX_ChatChannelMembers_ModeratorId",
                table: "ChatChannelMembers",
                column: "ModeratorId");

            migrationBuilder.AddForeignKey(
                name: "FK_ChatMessages_ArtistMembers_SenderArtistMemberId",
                table: "ChatMessages",
                column: "SenderArtistMemberId",
                principalTable: "ArtistMembers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_ChatMessages_Guests_SenderGuestId",
                table: "ChatMessages",
                column: "SenderGuestId",
                principalTable: "Guests",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ChatMessages_ArtistMembers_SenderArtistMemberId",
                table: "ChatMessages");

            migrationBuilder.DropForeignKey(
                name: "FK_ChatMessages_Guests_SenderGuestId",
                table: "ChatMessages");

            migrationBuilder.DropTable(
                name: "ChatChannelMembers");

            migrationBuilder.DropIndex(
                name: "IX_ChatMessages_SenderArtistMemberId",
                table: "ChatMessages");

            migrationBuilder.DropIndex(
                name: "IX_ChatMessages_SenderGuestId",
                table: "ChatMessages");

            migrationBuilder.DropColumn(
                name: "SenderArtistMemberId",
                table: "ChatMessages");

            migrationBuilder.DropColumn(
                name: "SenderGuestId",
                table: "ChatMessages");
        }
    }
}
