using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProjectTracker.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddImportedProjectCompletionPrompt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ImportNeedsCompletion",
                table: "Projects",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ImportNeedsCompletion",
                table: "Projects");
        }
    }
}
