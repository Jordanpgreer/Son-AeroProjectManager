using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QualityAssurance.Api.Data.Migrations;

[DbContext(typeof(QualityAssuranceDbContext))]
[Migration("20261015120000_ConvertQualityShipmentLineItemNumbersToString")]
public partial class ConvertQualityShipmentLineItemNumbersToString : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        if (ActiveProvider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            migrationBuilder.Sql(
                """
                CREATE TABLE "__QualityShipmentParts_LineItemText" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_QualityShipmentParts" PRIMARY KEY AUTOINCREMENT,
                    "ShipmentId" INTEGER NOT NULL,
                    "PartNumber" TEXT NOT NULL,
                    "Quantity" INTEGER NULL,
                    "UnitPrice" TEXT NULL,
                    "TotalValue" TEXT NULL,
                    "ExternalItemId" TEXT NULL,
                    "LineItemNumber" TEXT NULL,
                    "DisplayOrder" INTEGER NOT NULL,
                    CONSTRAINT "FK_QualityShipmentParts_QualityShipments_ShipmentId"
                        FOREIGN KEY ("ShipmentId") REFERENCES "QualityShipments" ("Id") ON DELETE CASCADE
                );
                INSERT INTO "__QualityShipmentParts_LineItemText"
                    ("Id", "ShipmentId", "PartNumber", "Quantity", "UnitPrice", "TotalValue",
                     "ExternalItemId", "LineItemNumber", "DisplayOrder")
                SELECT
                    "Id", "ShipmentId", "PartNumber", "Quantity", "UnitPrice", "TotalValue",
                    "ExternalItemId", CAST("LineItemNumber" AS TEXT), "DisplayOrder"
                FROM "QualityShipmentParts";
                DROP TABLE "QualityShipmentParts";
                ALTER TABLE "__QualityShipmentParts_LineItemText" RENAME TO "QualityShipmentParts";
                CREATE INDEX "IX_QualityShipmentParts_PartNumber"
                    ON "QualityShipmentParts" ("PartNumber");
                CREATE INDEX "IX_QualityShipmentParts_ShipmentId_DisplayOrder"
                    ON "QualityShipmentParts" ("ShipmentId", "DisplayOrder");
                """);
            return;
        }

        migrationBuilder.AlterColumn<string>(
            name: "LineItemNumber",
            table: "QualityShipmentParts",
            type: "nvarchar(80)",
            maxLength: 80,
            nullable: true,
            oldClrType: typeof(int),
            oldType: "int",
            oldNullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        if (ActiveProvider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            migrationBuilder.Sql(
                """
                CREATE TABLE "__QualityShipmentParts_LineItemInteger" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_QualityShipmentParts" PRIMARY KEY AUTOINCREMENT,
                    "ShipmentId" INTEGER NOT NULL,
                    "PartNumber" TEXT NOT NULL,
                    "Quantity" INTEGER NULL,
                    "UnitPrice" TEXT NULL,
                    "TotalValue" TEXT NULL,
                    "ExternalItemId" TEXT NULL,
                    "LineItemNumber" INTEGER NULL,
                    "DisplayOrder" INTEGER NOT NULL,
                    CONSTRAINT "FK_QualityShipmentParts_QualityShipments_ShipmentId"
                        FOREIGN KEY ("ShipmentId") REFERENCES "QualityShipments" ("Id") ON DELETE CASCADE
                );
                INSERT INTO "__QualityShipmentParts_LineItemInteger"
                    ("Id", "ShipmentId", "PartNumber", "Quantity", "UnitPrice", "TotalValue",
                     "ExternalItemId", "LineItemNumber", "DisplayOrder")
                SELECT
                    "Id", "ShipmentId", "PartNumber", "Quantity", "UnitPrice", "TotalValue",
                    "ExternalItemId", CAST("LineItemNumber" AS INTEGER), "DisplayOrder"
                FROM "QualityShipmentParts";
                DROP TABLE "QualityShipmentParts";
                ALTER TABLE "__QualityShipmentParts_LineItemInteger" RENAME TO "QualityShipmentParts";
                CREATE INDEX "IX_QualityShipmentParts_PartNumber"
                    ON "QualityShipmentParts" ("PartNumber");
                CREATE INDEX "IX_QualityShipmentParts_ShipmentId_DisplayOrder"
                    ON "QualityShipmentParts" ("ShipmentId", "DisplayOrder");
                """);
            return;
        }

        migrationBuilder.AlterColumn<int>(
            name: "LineItemNumber",
            table: "QualityShipmentParts",
            type: "int",
            nullable: true,
            oldClrType: typeof(string),
            oldType: "nvarchar(80)",
            oldMaxLength: 80,
            oldNullable: true);
    }
}
