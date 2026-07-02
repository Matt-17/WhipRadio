using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhipRadio.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AgentActionLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentActionLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    AgentName = table.Column<string>(type: "text", nullable: false),
                    ModeratorId = table.Column<int>(type: "integer", nullable: true),
                    Source = table.Column<string>(type: "text", nullable: false),
                    CorrelationId = table.Column<Guid>(type: "uuid", nullable: true),
                    Round = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    Tool = table.Column<string>(type: "text", nullable: true),
                    Content = table.Column<string>(type: "text", nullable: false),
                    Outcome = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentActionLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentActionLogs_AgentName_CreatedAtUtc",
                table: "AgentActionLogs",
                columns: new[] { "AgentName", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentActionLogs_CorrelationId",
                table: "AgentActionLogs",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentActionLogs_CreatedAtUtc",
                table: "AgentActionLogs",
                column: "CreatedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentActionLogs");
        }
    }
}
