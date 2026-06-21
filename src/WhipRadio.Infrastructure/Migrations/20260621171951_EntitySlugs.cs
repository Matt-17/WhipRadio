using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhipRadio.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class EntitySlugs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Slug",
                table: "Moderators",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Slug",
                table: "Artists",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql(
                """
                WITH SlugBase AS (
                    SELECT
                        Id,
                        CASE
                            WHEN trim(lower(replace(replace(replace(replace(replace(Name, '&', 'and'), '''', ''), '.', ''), '_', '-'), ' ', '-')), '-') = ''
                                THEN 'host-' || Id
                            ELSE trim(lower(replace(replace(replace(replace(replace(Name, '&', 'and'), '''', ''), '.', ''), '_', '-'), ' ', '-')), '-')
                        END AS BaseSlug
                    FROM Moderators
                ),
                Ranked AS (
                    SELECT
                        Id,
                        BaseSlug,
                        ROW_NUMBER() OVER (PARTITION BY BaseSlug ORDER BY Id) AS RowNumber
                    FROM SlugBase
                )
                UPDATE Moderators
                SET Slug = (
                    SELECT BaseSlug || CASE WHEN RowNumber = 1 THEN '' ELSE '-' || RowNumber END
                    FROM Ranked
                    WHERE Ranked.Id = Moderators.Id
                );
                """);

            migrationBuilder.Sql(
                """
                WITH SlugBase AS (
                    SELECT
                        Id,
                        CASE
                            WHEN trim(lower(replace(replace(replace(replace(replace(Name, '&', 'and'), '''', ''), '.', ''), '_', '-'), ' ', '-')), '-') = ''
                                THEN 'artist-' || substr(Id, 1, 8)
                            ELSE trim(lower(replace(replace(replace(replace(replace(Name, '&', 'and'), '''', ''), '.', ''), '_', '-'), ' ', '-')), '-')
                        END AS BaseSlug
                    FROM Artists
                ),
                Ranked AS (
                    SELECT
                        Id,
                        BaseSlug,
                        ROW_NUMBER() OVER (PARTITION BY BaseSlug ORDER BY Id) AS RowNumber
                    FROM SlugBase
                )
                UPDATE Artists
                SET Slug = (
                    SELECT BaseSlug || CASE WHEN RowNumber = 1 THEN '' ELSE '-' || RowNumber END
                    FROM Ranked
                    WHERE Ranked.Id = Artists.Id
                );
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Moderators_Slug",
                table: "Moderators",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Artists_Slug",
                table: "Artists",
                column: "Slug",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Moderators_Slug",
                table: "Moderators");

            migrationBuilder.DropIndex(
                name: "IX_Artists_Slug",
                table: "Artists");

            migrationBuilder.DropColumn(
                name: "Slug",
                table: "Moderators");

            migrationBuilder.DropColumn(
                name: "Slug",
                table: "Artists");
        }
    }
}
