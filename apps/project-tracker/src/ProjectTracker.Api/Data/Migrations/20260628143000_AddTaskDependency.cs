using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;

#nullable disable

namespace ProjectTracker.Api.Data.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(ProjectTrackerDbContext))]
    [Migration("20260628143000_AddTaskDependency")]
    public partial class AddTaskDependency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DependencyTaskId",
                table: "Tasks",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_DependencyTaskId",
                table: "Tasks",
                column: "DependencyTaskId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Tasks_DependencyTaskId",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "DependencyTaskId",
                table: "Tasks");
        }
    }
}
