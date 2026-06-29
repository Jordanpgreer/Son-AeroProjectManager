using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProjectTracker.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskNoteUpdatedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NoteUpdatedAt",
                table: "Tasks",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE Tasks
                SET NoteUpdatedAt = UpdatedAt
                WHERE Notes IS NOT NULL AND LTRIM(RTRIM(Notes)) <> ''
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NoteUpdatedAt",
                table: "Tasks");
        }
    }
}
