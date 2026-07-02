using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhipRadio.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Phase4Chat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ChatHistoryPromptMessages",
                table: "StationSettings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ChatMaxAgentHops",
                table: "StationSettings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ChatRetainedMessagesPerChannel",
                table: "StationSettings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "ChatChannels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    ModeratorId = table.Column<int>(type: "integer", nullable: true),
                    CounterpartModeratorId = table.Column<int>(type: "integer", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    LastMessageAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    AdminLastReadAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    IsArchived = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatChannels", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChatChannels_Moderators_CounterpartModeratorId",
                        column: x => x.CounterpartModeratorId,
                        principalTable: "Moderators",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ChatChannels_Moderators_ModeratorId",
                        column: x => x.ModeratorId,
                        principalTable: "Moderators",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ProgramDirectorLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Source = table.Column<string>(type: "text", nullable: false),
                    PromptSummary = table.Column<string>(type: "text", nullable: false),
                    ActionsJson = table.Column<string>(type: "text", nullable: true),
                    Outcome = table.Column<string>(type: "text", nullable: false),
                    Error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProgramDirectorLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ChatMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ChannelId = table.Column<Guid>(type: "uuid", nullable: false),
                    SenderKind = table.Column<string>(type: "text", nullable: false),
                    SenderModeratorId = table.Column<int>(type: "integer", nullable: true),
                    Text = table.Column<string>(type: "text", nullable: false),
                    ActionsJson = table.Column<string>(type: "text", nullable: true),
                    CorrelationId = table.Column<Guid>(type: "uuid", nullable: true),
                    HopCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChatMessages_ChatChannels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "ChatChannels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ChatMessages_Moderators_SenderModeratorId",
                        column: x => x.SenderModeratorId,
                        principalTable: "Moderators",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChatChannels_CounterpartModeratorId",
                table: "ChatChannels",
                column: "CounterpartModeratorId");

            migrationBuilder.CreateIndex(
                name: "IX_ChatChannels_Kind_ModeratorId_CounterpartModeratorId",
                table: "ChatChannels",
                columns: new[] { "Kind", "ModeratorId", "CounterpartModeratorId" });

            migrationBuilder.CreateIndex(
                name: "IX_ChatChannels_LastMessageAtUtc",
                table: "ChatChannels",
                column: "LastMessageAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ChatChannels_ModeratorId",
                table: "ChatChannels",
                column: "ModeratorId");

            migrationBuilder.CreateIndex(
                name: "IX_ChatMessages_ChannelId_CreatedAtUtc",
                table: "ChatMessages",
                columns: new[] { "ChannelId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ChatMessages_CorrelationId",
                table: "ChatMessages",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_ChatMessages_SenderModeratorId",
                table: "ChatMessages",
                column: "SenderModeratorId");

            migrationBuilder.CreateIndex(
                name: "IX_ProgramDirectorLogs_Source_CreatedAtUtc",
                table: "ProgramDirectorLogs",
                columns: new[] { "Source", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChatMessages");

            migrationBuilder.DropTable(
                name: "ProgramDirectorLogs");

            migrationBuilder.DropTable(
                name: "ChatChannels");

            migrationBuilder.DropColumn(
                name: "ChatHistoryPromptMessages",
                table: "StationSettings");

            migrationBuilder.DropColumn(
                name: "ChatMaxAgentHops",
                table: "StationSettings");

            migrationBuilder.DropColumn(
                name: "ChatRetainedMessagesPerChannel",
                table: "StationSettings");
        }
    }
}
