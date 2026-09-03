using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProjectTracker.Api.Data.Migrations;

[DbContext(typeof(ProjectTrackerDbContext))]
[Migration("20260903160000_AddSalesPersonAndProjectNotificationPreferences")]
public partial class AddSalesPersonAndProjectNotificationPreferences : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "SalesPerson",
            table: "Projects",
            type: "nvarchar(120)",
            maxLength: 120,
            nullable: true);

        migrationBuilder.AddColumn<DateOnly>(
            name: "ExternalActualStartDate",
            table: "Tasks",
            type: "date",
            nullable: true);

        migrationBuilder.AddColumn<DateOnly>(
            name: "ExternalActualCompletionDate",
            table: "Tasks",
            type: "date",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "ProjectNotificationPreferences",
            columns: table => new
            {
                ProjectId = table.Column<int>(type: "int", nullable: false),
                AppUserId = table.Column<int>(type: "int", nullable: false),
                Enabled = table.Column<bool>(type: "bit", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                UpdatedByAccountName = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ProjectNotificationPreferences", x => new { x.ProjectId, x.AppUserId });
                table.ForeignKey(
                    name: "FK_ProjectNotificationPreferences_Projects_ProjectId",
                    column: x => x.ProjectId,
                    principalTable: "Projects",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_ProjectNotificationPreferences_Users_AppUserId",
                    column: x => x.AppUserId,
                    principalTable: "Users",
                    principalColumn: "Id");
            });

        migrationBuilder.CreateIndex(
            name: "IX_ProjectNotificationPreferences_AppUserId",
            table: "ProjectNotificationPreferences",
            column: "AppUserId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "ProjectNotificationPreferences");
        migrationBuilder.DropColumn(name: "ExternalActualCompletionDate", table: "Tasks");
        migrationBuilder.DropColumn(name: "ExternalActualStartDate", table: "Tasks");
        migrationBuilder.DropColumn(name: "SalesPerson", table: "Projects");
    }
}
