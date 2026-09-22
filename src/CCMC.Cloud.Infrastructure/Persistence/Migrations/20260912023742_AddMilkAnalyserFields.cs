using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CCMC.Cloud.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMilkAnalyserFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "Clr",
                table: "milk_reception_transactions",
                type: "numeric(5,2)",
                precision: 5,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Protein",
                table: "milk_reception_transactions",
                type: "numeric(5,2)",
                precision: 5,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RawAnalyserPayload",
                table: "milk_reception_transactions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Water",
                table: "milk_reception_transactions",
                type: "numeric(5,2)",
                precision: 5,
                scale: 2,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Clr",
                table: "milk_reception_transactions");

            migrationBuilder.DropColumn(
                name: "Protein",
                table: "milk_reception_transactions");

            migrationBuilder.DropColumn(
                name: "RawAnalyserPayload",
                table: "milk_reception_transactions");

            migrationBuilder.DropColumn(
                name: "Water",
                table: "milk_reception_transactions");
        }
    }
}
