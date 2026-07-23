using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProjectTracker.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class EnforceExplicitProgressAndTaskDependencies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE [task]
                SET [DependencyTaskId] = NULL
                FROM [Tasks] AS [task]
                LEFT JOIN [Tasks] AS [dependency] ON [dependency].[Id] = [task].[DependencyTaskId]
                WHERE [task].[DependencyTaskId] IS NOT NULL
                  AND (
                      [dependency].[Id] IS NULL
                      OR [dependency].[Id] = [task].[Id]
                      OR [dependency].[ProjectId] <> [task].[ProjectId]
                      OR [dependency].[Sequence] >= [task].[Sequence]
                  );

                UPDATE [Tasks]
                SET [PercentCompleteManual] = CAST(1 AS bit);
                """);

            migrationBuilder.AddForeignKey(
                name: "FK_Tasks_Tasks_DependencyTaskId",
                table: "Tasks",
                column: "DependencyTaskId",
                principalTable: "Tasks",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Tasks_Tasks_DependencyTaskId",
                table: "Tasks");
        }
    }
}
