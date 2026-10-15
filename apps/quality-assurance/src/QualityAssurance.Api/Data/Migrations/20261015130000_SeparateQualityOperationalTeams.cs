using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QualityAssurance.Api.Data.Migrations;

[DbContext(typeof(QualityAssuranceDbContext))]
[Migration("20261015130000_SeparateQualityOperationalTeams")]
public partial class SeparateQualityOperationalTeams : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "UsesOperationalTeam",
            table: "QualityAssignmentRules",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name: "IsOperationalTeamAssignment",
            table: "QualityShipments",
            nullable: false,
            defaultValue: false);

        migrationBuilder.CreateTable(
            name: "QualityTeams",
            columns: table => new
            {
                Id = table.Column<int>(nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1")
                    .Annotation("Sqlite:Autoincrement", true),
                Name = table.Column<string>(maxLength: 120, nullable: false),
                Description = table.Column<string>(maxLength: 500, nullable: true),
                IsActive = table.Column<bool>(nullable: false),
                Version = table.Column<long>(nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(nullable: false),
                CreatedBy = table.Column<string>(maxLength: 160, nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(nullable: false),
                UpdatedBy = table.Column<string>(maxLength: 160, nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_QualityTeams", x => x.Id));

        migrationBuilder.CreateTable(
            name: "QualityTeamMembers",
            columns: table => new
            {
                QualityTeamId = table.Column<int>(nullable: false),
                AppUserId = table.Column<int>(nullable: false),
                AccountName = table.Column<string>(maxLength: 160, nullable: false),
                DisplayName = table.Column<string>(maxLength: 160, nullable: false),
                IsManager = table.Column<bool>(nullable: false),
                AddedAt = table.Column<DateTimeOffset>(nullable: false),
                AddedBy = table.Column<string>(maxLength: 160, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_QualityTeamMembers", x => new { x.QualityTeamId, x.AppUserId });
                table.ForeignKey(
                    name: "FK_QualityTeamMembers_QualityTeams_QualityTeamId",
                    column: x => x.QualityTeamId,
                    principalTable: "QualityTeams",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_QualityTeams_Name",
            table: "QualityTeams",
            column: "Name",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_QualityTeamMembers_AppUserId_IsManager",
            table: "QualityTeamMembers",
            columns: new[] { "AppUserId", "IsManager" });

        migrationBuilder.DropIndex(
            name: "IX_QualityShipments_IsShipped_AssignedGroupId_ShipDate",
            table: "QualityShipments");

        migrationBuilder.DropIndex(
            name: "IX_QualityShipments_IsShipped_AssignedUserId_CreatedAt",
            table: "QualityShipments");

        migrationBuilder.CreateIndex(
            name: "IX_QualityShipments_IsShipped_IsOperationalTeamAssignment_AssignedGroupId_ShipDate",
            table: "QualityShipments",
            columns: new[] { "IsShipped", "IsOperationalTeamAssignment", "AssignedGroupId", "ShipDate" });

        migrationBuilder.CreateIndex(
            name: "IX_QualityShipments_IsShipped_IsOperationalTeamAssignment_AssignedUserId_CreatedAt",
            table: "QualityShipments",
            columns: new[] { "IsShipped", "IsOperationalTeamAssignment", "AssignedUserId", "CreatedAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "QualityTeamMembers");
        migrationBuilder.DropTable(name: "QualityTeams");

        migrationBuilder.DropIndex(
            name: "IX_QualityShipments_IsShipped_IsOperationalTeamAssignment_AssignedGroupId_ShipDate",
            table: "QualityShipments");
        migrationBuilder.DropIndex(
            name: "IX_QualityShipments_IsShipped_IsOperationalTeamAssignment_AssignedUserId_CreatedAt",
            table: "QualityShipments");

        migrationBuilder.DropColumn(name: "UsesOperationalTeam", table: "QualityAssignmentRules");
        migrationBuilder.DropColumn(name: "IsOperationalTeamAssignment", table: "QualityShipments");

        migrationBuilder.CreateIndex(
            name: "IX_QualityShipments_IsShipped_AssignedGroupId_ShipDate",
            table: "QualityShipments",
            columns: new[] { "IsShipped", "AssignedGroupId", "ShipDate" });
        migrationBuilder.CreateIndex(
            name: "IX_QualityShipments_IsShipped_AssignedUserId_CreatedAt",
            table: "QualityShipments",
            columns: new[] { "IsShipped", "AssignedUserId", "CreatedAt" });
    }
}
