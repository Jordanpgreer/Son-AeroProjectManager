using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProjectTracker.Api.Data;

#nullable disable

namespace ProjectTracker.Api.Data.Migrations
{
    [DbContext(typeof(ProjectTrackerDbContext))]
    [Migration("20260902000000_AddProjectQuantities")]
    public partial class AddProjectQuantities : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "RequiredQuantity",
                table: "Projects",
                type: "decimal(18,4)",
                nullable: true);
            migrationBuilder.AddColumn<decimal>(
                name: "JobQuantity",
                table: "Projects",
                type: "decimal(18,4)",
                nullable: true);
            migrationBuilder.AddColumn<string>(
                name: "RequiredQuantitySource",
                table: "Projects",
                type: "nvarchar(24)",
                maxLength: 24,
                nullable: true);
            migrationBuilder.AddColumn<string>(
                name: "JobQuantitySource",
                table: "Projects",
                type: "nvarchar(24)",
                maxLength: 24,
                nullable: true);
            migrationBuilder.AddColumn<string>(
                name: "QuantityLastSyncProvider",
                table: "Projects",
                type: "nvarchar(24)",
                maxLength: 24,
                nullable: true);
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "QuantityLastSyncedAt",
                table: "Projects",
                type: "datetimeoffset",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "RequiredQuantity", table: "Projects");
            migrationBuilder.DropColumn(name: "JobQuantity", table: "Projects");
            migrationBuilder.DropColumn(name: "RequiredQuantitySource", table: "Projects");
            migrationBuilder.DropColumn(name: "JobQuantitySource", table: "Projects");
            migrationBuilder.DropColumn(name: "QuantityLastSyncProvider", table: "Projects");
            migrationBuilder.DropColumn(name: "QuantityLastSyncedAt", table: "Projects");
        }
    }
}
