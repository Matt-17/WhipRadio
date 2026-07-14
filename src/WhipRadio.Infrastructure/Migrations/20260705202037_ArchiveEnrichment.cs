using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhipRadio.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ArchiveEnrichment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExternalIds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerType = table.Column<int>(type: "integer", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<string>(type: "text", nullable: false),
                    EntityType = table.Column<string>(type: "text", nullable: false),
                    Value = table.Column<string>(type: "text", nullable: false),
                    Confidence = table.Column<double>(type: "double precision", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalIds", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "KnowledgeEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EntityKind = table.Column<string>(type: "text", nullable: false),
                    DisplayName = table.Column<string>(type: "text", nullable: false),
                    Source = table.Column<string>(type: "text", nullable: false),
                    SourceEntityId = table.Column<string>(type: "text", nullable: false),
                    FactsJson = table.Column<string>(type: "text", nullable: false),
                    Digest = table.Column<string>(type: "text", nullable: false),
                    LicenseClass = table.Column<int>(type: "integer", nullable: false),
                    RetrievedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnowledgeEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MetadataCandidates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TrackId = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<string>(type: "text", nullable: false),
                    SourceEntityId = table.Column<string>(type: "text", nullable: false),
                    DisplayTitle = table.Column<string>(type: "text", nullable: false),
                    DisplayArtist = table.Column<string>(type: "text", nullable: false),
                    DisplayAlbum = table.Column<string>(type: "text", nullable: true),
                    DisplayYear = table.Column<int>(type: "integer", nullable: true),
                    ArtistEntityId = table.Column<string>(type: "text", nullable: true),
                    Score = table.Column<double>(type: "double precision", nullable: false),
                    ReasonsJson = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetadataCandidates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MetadataClaims",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerType = table.Column<int>(type: "integer", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    FieldName = table.Column<string>(type: "text", nullable: false),
                    Value = table.Column<string>(type: "text", nullable: false),
                    Source = table.Column<string>(type: "text", nullable: false),
                    SourceEntityId = table.Column<string>(type: "text", nullable: true),
                    LicenseClass = table.Column<int>(type: "integer", nullable: false),
                    Confidence = table.Column<double>(type: "double precision", nullable: false),
                    IsApplied = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetadataClaims", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExternalIds_OwnerType_OwnerId",
                table: "ExternalIds",
                columns: new[] { "OwnerType", "OwnerId" });

            migrationBuilder.CreateIndex(
                name: "IX_ExternalIds_Source_Value",
                table: "ExternalIds",
                columns: new[] { "Source", "Value" });

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeEntries_DisplayName",
                table: "KnowledgeEntries",
                column: "DisplayName");

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeEntries_Source_SourceEntityId",
                table: "KnowledgeEntries",
                columns: new[] { "Source", "SourceEntityId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MetadataCandidates_TrackId",
                table: "MetadataCandidates",
                column: "TrackId");

            migrationBuilder.CreateIndex(
                name: "IX_MetadataClaims_OwnerType_OwnerId",
                table: "MetadataClaims",
                columns: new[] { "OwnerType", "OwnerId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExternalIds");

            migrationBuilder.DropTable(
                name: "KnowledgeEntries");

            migrationBuilder.DropTable(
                name: "MetadataCandidates");

            migrationBuilder.DropTable(
                name: "MetadataClaims");
        }
    }
}
