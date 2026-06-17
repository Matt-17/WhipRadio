using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using WhipRadio.Infrastructure.Persistence;

#nullable disable

namespace WhipRadio.Infrastructure.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(RadioDbContext))]
    [Migration("20260617150000_Phase3bPersonalityTraits")]
    public partial class Phase3bPersonalityTraits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BaselineEnergy",
                table: "Moderators",
                type: "TEXT",
                nullable: false,
                defaultValue: "Medium");

            migrationBuilder.AddColumn<string>(
                name: "BaselineFormality",
                table: "Moderators",
                type: "TEXT",
                nullable: false,
                defaultValue: "Balanced");

            migrationBuilder.AddColumn<string>(
                name: "BaselineHumorLevel",
                table: "Moderators",
                type: "TEXT",
                nullable: false,
                defaultValue: "Medium");

            migrationBuilder.AddColumn<string>(
                name: "BaselineTalkativeness",
                table: "Moderators",
                type: "TEXT",
                nullable: false,
                defaultValue: "Medium");

            migrationBuilder.AddColumn<string>(
                name: "BaselineWarmth",
                table: "Moderators",
                type: "TEXT",
                nullable: false,
                defaultValue: "Medium");

            migrationBuilder.Sql("""
                UPDATE Moderators
                SET BaselineTalkativeness = CASE
                    WHEN Talkativeness < 0.2 THEN 'VeryLow'
                    WHEN Talkativeness < 0.4 THEN 'Low'
                    WHEN Talkativeness > 0.8 THEN 'VeryHigh'
                    WHEN Talkativeness > 0.6 THEN 'High'
                    ELSE 'Medium'
                END;
                """);

            migrationBuilder.Sql("""
                UPDATE Moderators
                SET BaselineEnergy = CASE
                    WHEN lower(Style) LIKE '%fast%' OR lower(Style) LIKE '%energetic%' OR lower(Style) LIKE '%bubbly%' OR lower(Style) LIKE '%chatty%' THEN 'High'
                    WHEN lower(Style) LIKE '%slow%' OR lower(Style) LIKE '%calm%' OR lower(Style) LIKE '%laid%' OR lower(Style) LIKE '%thoughtful%' THEN 'Low'
                    ELSE 'Medium'
                END;
                """);

            migrationBuilder.Sql("""
                UPDATE Moderators
                SET BaselineFormality = CASE
                    WHEN lower(Style) LIKE '%formal%' OR lower(Style) LIKE '%measured%' OR lower(Style) LIKE '%thoughtful%' THEN 'Formal'
                    WHEN lower(Style) LIKE '%casual%' OR lower(Style) LIKE '%laid%' OR lower(Style) LIKE '%beach%' THEN 'Casual'
                    ELSE 'Balanced'
                END;
                """);

            migrationBuilder.Sql("""
                UPDATE Moderators
                SET BaselineHumorLevel = CASE
                    WHEN lower(Style) LIKE '%dry%' OR lower(Style) LIKE '%pun%' OR lower(Style) LIKE '%funny%' OR lower(Style) LIKE '%humor%' OR lower(Style) LIKE '%witty%' THEN 'High'
                    ELSE 'Medium'
                END;
                """);

            migrationBuilder.Sql("""
                UPDATE Moderators
                SET BaselineWarmth = CASE
                    WHEN lower(Style) LIKE '%warm%' OR lower(Style) LIKE '%friendly%' OR lower(Style) LIKE '%bubbly%' OR lower(Style) LIKE '%late-night%' THEN 'High'
                    ELSE 'Medium'
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BaselineEnergy",
                table: "Moderators");

            migrationBuilder.DropColumn(
                name: "BaselineFormality",
                table: "Moderators");

            migrationBuilder.DropColumn(
                name: "BaselineHumorLevel",
                table: "Moderators");

            migrationBuilder.DropColumn(
                name: "BaselineTalkativeness",
                table: "Moderators");

            migrationBuilder.DropColumn(
                name: "BaselineWarmth",
                table: "Moderators");
        }
    }
}
