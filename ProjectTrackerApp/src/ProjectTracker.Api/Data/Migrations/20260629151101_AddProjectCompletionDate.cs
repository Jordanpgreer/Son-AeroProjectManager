using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProjectTracker.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectCompletionDate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "CompletedOn",
                table: "Projects",
                type: "date",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE Projects
                SET CompletedOn = COALESCE(
                    (SELECT MAX(EndDate) FROM Tasks WHERE Tasks.ProjectId = Projects.Id),
                    CAST(UpdatedAt AS date))
                WHERE Status = 'Complete' AND CompletedOn IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CompletedOn",
                table: "Projects");
        }
    }
}
