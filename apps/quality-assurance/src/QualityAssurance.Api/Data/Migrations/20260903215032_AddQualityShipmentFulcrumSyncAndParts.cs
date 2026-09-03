using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QualityAssurance.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddQualityShipmentFulcrumSyncAndParts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExternalShipmentId",
                table: "QualityShipments",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalShipmentStatus",
                table: "QualityShipments",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalShipmentUrl",
                table: "QualityShipments",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalSyncError",
                table: "QualityShipments",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalSyncProvider",
                table: "QualityShipments",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ExternalSyncedAt",
                table: "QualityShipments",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "QualityShipmentParts",
                columns: table => new
                {
                    Id = table.Column<int>(
                            type: ActiveProvider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase)
                                ? "INTEGER"
                                : "int",
                            nullable: false)
                        .Annotation("Sqlite:Autoincrement", true)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ShipmentId = table.Column<int>(nullable: false),
                    PartNumber = table.Column<string>(maxLength: 160, nullable: false),
                    Quantity = table.Column<int>(nullable: true),
                    UnitPrice = table.Column<decimal>(precision: 18, scale: 2, nullable: true),
                    TotalValue = table.Column<decimal>(precision: 18, scale: 2, nullable: true),
                    ExternalItemId = table.Column<string>(maxLength: 80, nullable: true),
                    DisplayOrder = table.Column<int>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QualityShipmentParts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QualityShipmentParts_QualityShipments_ShipmentId",
                        column: x => x.ShipmentId,
                        principalTable: "QualityShipments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            var isSqlite = ActiveProvider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase);
            migrationBuilder.Sql(
                isSqlite
                    ? """
                INSERT INTO QualityShipmentParts
                    (ShipmentId, PartNumber, Quantity, UnitPrice, TotalValue, ExternalItemId, DisplayOrder)
                SELECT
                    Id,
                    PartNumber,
                    CASE
                        WHEN CAST(Quantity AS REAL) >= 0
                            AND CAST(Quantity AS REAL) <= 2147483647
                            AND CAST(Quantity AS REAL) = ROUND(CAST(Quantity AS REAL), 0)
                        THEN CAST(Quantity AS INTEGER)
                        ELSE NULL
                    END,
                    CASE
                        WHEN CAST(Quantity AS REAL) > 0
                            AND CAST(Quantity AS REAL) <= 2147483647
                            AND CAST(Quantity AS REAL) = ROUND(CAST(Quantity AS REAL), 0)
                            AND DollarValue IS NOT NULL
                        THEN ROUND(CAST(DollarValue AS REAL) / CAST(Quantity AS REAL), 2)
                        ELSE NULL
                    END,
                    DollarValue,
                    NULL,
                    0
                FROM QualityShipments
                WHERE PartNumber IS NOT NULL AND TRIM(PartNumber) <> '';
                """
                    : """
                INSERT INTO QualityShipmentParts
                    (ShipmentId, PartNumber, Quantity, UnitPrice, TotalValue, ExternalItemId, DisplayOrder)
                SELECT
                    Id,
                    PartNumber,
                    CASE
                        WHEN Quantity >= 0
                            AND Quantity <= 2147483647
                            AND Quantity = ROUND(Quantity, 0)
                        THEN CAST(Quantity AS int)
                        ELSE NULL
                    END,
                    CASE
                        WHEN Quantity > 0
                            AND Quantity <= 2147483647
                            AND Quantity = ROUND(Quantity, 0)
                            AND DollarValue IS NOT NULL
                        THEN ROUND(DollarValue / Quantity, 2)
                        ELSE NULL
                    END,
                    DollarValue,
                    NULL,
                    0
                FROM QualityShipments
                WHERE PartNumber IS NOT NULL AND TRIM(PartNumber) <> '';
                """);

            migrationBuilder.CreateIndex(
                name: "IX_QualityShipments_ExternalShipmentId",
                table: "QualityShipments",
                column: "ExternalShipmentId");

            migrationBuilder.CreateIndex(
                name: "IX_QualityShipmentParts_PartNumber",
                table: "QualityShipmentParts",
                column: "PartNumber");

            migrationBuilder.CreateIndex(
                name: "IX_QualityShipmentParts_ShipmentId_DisplayOrder",
                table: "QualityShipmentParts",
                columns: new[] { "ShipmentId", "DisplayOrder" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "QualityShipmentParts");

            migrationBuilder.DropIndex(
                name: "IX_QualityShipments_ExternalShipmentId",
                table: "QualityShipments");

            migrationBuilder.DropColumn(
                name: "ExternalShipmentId",
                table: "QualityShipments");

            migrationBuilder.DropColumn(
                name: "ExternalShipmentStatus",
                table: "QualityShipments");

            migrationBuilder.DropColumn(
                name: "ExternalShipmentUrl",
                table: "QualityShipments");

            migrationBuilder.DropColumn(
                name: "ExternalSyncError",
                table: "QualityShipments");

            migrationBuilder.DropColumn(
                name: "ExternalSyncProvider",
                table: "QualityShipments");

            migrationBuilder.DropColumn(
                name: "ExternalSyncedAt",
                table: "QualityShipments");
        }
    }
}
