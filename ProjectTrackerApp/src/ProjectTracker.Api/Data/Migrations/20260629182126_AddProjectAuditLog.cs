using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProjectTracker.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectAuditLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProjectAuditEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    ProjectTaskId = table.Column<int>(type: "int", nullable: true),
                    Action = table.Column<string>(type: "nvarchar(48)", maxLength: 48, nullable: false),
                    Summary = table.Column<string>(type: "nvarchar(240)", maxLength: 240, nullable: false),
                    ChangesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ChangedByAccountName = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    ChangedByDisplayName = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    ChangedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectAuditEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectAuditEntries_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectAuditEntries_ProjectId_ChangedAt",
                table: "ProjectAuditEntries",
                columns: new[] { "ProjectId", "ChangedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProjectAuditEntries");
        }
    }
}
