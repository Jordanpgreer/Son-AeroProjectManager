using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProjectTracker.Api.Data;

#nullable disable

namespace ProjectTracker.Api.Data.Migrations
{
    [DbContext(typeof(ProjectTrackerDbContext))]
    [Migration("20260824002000_AddFeatureSettings")]
    public partial class AddFeatureSettings : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FeatureSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    WalkthroughEnabled = table.Column<bool>(type: "bit", nullable: false),
                    AssistantEnabled = table.Column<bool>(type: "bit", nullable: false),
                    AssistantName = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeatureSettings", x => x.Id);
                });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "FeatureSettings");
        }
    }
}
