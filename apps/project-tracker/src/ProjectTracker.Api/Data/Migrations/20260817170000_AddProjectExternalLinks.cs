using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProjectTracker.Api.Data.Migrations;

[DbContext(typeof(ProjectTrackerDbContext))]
[Migration("20260817170000_AddProjectExternalLinks")]
public partial class AddProjectExternalLinks : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "SalesOrderUrl",
            table: "Projects",
            type: "nvarchar(2048)",
            maxLength: 2048,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "JobUrl",
            table: "Projects",
            type: "nvarchar(2048)",
            maxLength: 2048,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "SalesOrderUrl",
            table: "Projects");

        migrationBuilder.DropColumn(
            name: "JobUrl",
            table: "Projects");
    }
}
