using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProjectTracker.Api.Data.Migrations;

[DbContext(typeof(ProjectTrackerDbContext))]
[Migration("20260902170000_AddExternalOperationLinks")]
public partial class AddExternalOperationLinks : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ExternalSourceOperationId",
            table: "Tasks",
            type: "nvarchar(80)",
            maxLength: 80,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ExternalSourceProvider",
            table: "Tasks",
            type: "nvarchar(24)",
            maxLength: 24,
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_Tasks_ProjectId_ExternalSourceProvider_ExternalSourceOperationId",
            table: "Tasks",
            columns: new[] { "ProjectId", "ExternalSourceProvider", "ExternalSourceOperationId" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Tasks_ProjectId_ExternalSourceProvider_ExternalSourceOperationId",
            table: "Tasks");

        migrationBuilder.DropColumn(
            name: "ExternalSourceOperationId",
            table: "Tasks");

        migrationBuilder.DropColumn(
            name: "ExternalSourceProvider",
            table: "Tasks");
    }
}
