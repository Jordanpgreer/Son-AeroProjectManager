using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QualityAssurance.Api.Data.Migrations;

[DbContext(typeof(QualityAssuranceDbContext))]
[Migration("20260908193000_AddQualityShipmentShipperNumber")]
public partial class AddQualityShipmentShipperNumber : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ShipperNumber",
            table: "QualityShipments",
            maxLength: 80,
            nullable: true);

        // Preserve the prior release's behavior: the only available field was
        // displayed and synchronized as Shipper Number even though its storage
        // column was named SalesOrderNumber. Users can now correct either value.
        migrationBuilder.Sql(
            "UPDATE QualityShipments SET ShipperNumber = SalesOrderNumber " +
            "WHERE ShipperNumber IS NULL AND SalesOrderNumber IS NOT NULL;");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "ShipperNumber",
            table: "QualityShipments");
    }
}
