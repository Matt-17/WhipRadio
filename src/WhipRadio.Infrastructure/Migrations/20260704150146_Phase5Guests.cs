using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhipRadio.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Phase5Guests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Guests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Slug = table.Column<string>(type: "text", nullable: false),
                    Expertise = table.Column<string>(type: "text", nullable: false),
                    Gender = table.Column<string>(type: "text", nullable: false),
                    Age = table.Column<int>(type: "integer", nullable: true),
                    Interests = table.Column<string>(type: "text", nullable: false),
                    Personality = table.Column<string>(type: "text", nullable: false),
                    Biography = table.Column<string>(type: "text", nullable: false),
                    DeepBackground = table.Column<string>(type: "text", nullable: false),
                    CreationHint = table.Column<string>(type: "text", nullable: true),
                    GenerationPrompt = table.Column<string>(type: "text", nullable: true),
                    TtsEngine = table.Column<string>(type: "text", nullable: false, defaultValue: "qwen"),
                    VoiceId = table.Column<string>(type: "text", nullable: true),
                    VoiceCreationPrompt = table.Column<string>(type: "text", nullable: false),
                    VoiceReferencePath = table.Column<string>(type: "text", nullable: true),
                    VoiceDesignedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    VoiceDesignLastError = table.Column<string>(type: "text", nullable: true),
                    IsArchived = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Guests", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Guests_IsArchived",
                table: "Guests",
                column: "IsArchived");

            migrationBuilder.CreateIndex(
                name: "IX_Guests_Slug",
                table: "Guests",
                column: "Slug",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Guests");
        }
    }
}
