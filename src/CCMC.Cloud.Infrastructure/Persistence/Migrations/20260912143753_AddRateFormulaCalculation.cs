using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace CCMC.Cloud.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRateFormulaCalculation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "Amount",
                table: "milk_reception_transactions",
                type: "numeric(14,2)",
                precision: 14,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Rate",
                table: "milk_reception_transactions",
                type: "numeric(10,2)",
                precision: 10,
                scale: 2,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "rate_formula_settings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RateType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Value1 = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    Value2 = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    TsRate = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    CentreId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rate_formula_settings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_rate_formula_settings_chilling_centres_CentreId",
                        column: x => x.CentreId,
                        principalTable: "chilling_centres",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_rate_formula_settings_CentreId",
                table: "rate_formula_settings",
                column: "CentreId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "rate_formula_settings");

            migrationBuilder.DropColumn(
                name: "Amount",
                table: "milk_reception_transactions");

            migrationBuilder.DropColumn(
                name: "Rate",
                table: "milk_reception_transactions");
        }
    }
}
