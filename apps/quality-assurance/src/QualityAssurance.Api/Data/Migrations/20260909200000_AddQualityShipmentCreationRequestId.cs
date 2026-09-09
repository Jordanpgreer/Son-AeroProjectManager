using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QualityAssurance.Api.Data.Migrations;

[DbContext(typeof(QualityAssuranceDbContext))]
[Migration("20260909200000_AddQualityShipmentCreationRequestId")]
public sealed class AddQualityShipmentCreationRequestId : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "CreationRequestId",
            table: "QualityShipments",
            type: "uniqueidentifier",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_QualityShipments_CreationRequestId",
            table: "QualityShipments",
            column: "CreationRequestId",
            unique: true,
            filter: "[CreationRequestId] IS NOT NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_QualityShipments_CreationRequestId",
            table: "QualityShipments");

        migrationBuilder.DropColumn(
            name: "CreationRequestId",
            table: "QualityShipments");
    }
}
