using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProjectTracker.Api.Data.Migrations
{
    [DbContext(typeof(ProjectTrackerDbContext))]
    [Migration("20260902133000_AddBennyIdleSettings")]
    public partial class AddBennyIdleSettings : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AssistantIdleDelayMinutes",
                table: "FeatureSettings",
                type: "int",
                nullable: false,
                defaultValue: 10);

            migrationBuilder.AddColumn<string>(
                name: "AssistantIdleModules",
                table: "FeatureSettings",
                type: "nvarchar(240)",
                maxLength: 240,
                nullable: false,
                defaultValue: "project-tracker");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AssistantIdleDelayMinutes",
                table: "FeatureSettings");

            migrationBuilder.DropColumn(
                name: "AssistantIdleModules",
                table: "FeatureSettings");
        }
    }
}
