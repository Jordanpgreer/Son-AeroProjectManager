using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QualityAssurance.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddQualityWorkflows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "QualityWorkflowAuditEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: ActiveProvider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) ? "INTEGER" : "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1").Annotation("Sqlite:Autoincrement", true),
                    Module = table.Column<string>(maxLength: 80, nullable: false),
                    Action = table.Column<string>(maxLength: 40, nullable: false),
                    Revision = table.Column<int>(nullable: false),
                    GraphJson = table.Column<string>(type: ActiveProvider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) ? "TEXT" : "nvarchar(max)", nullable: false),
                    AccountName = table.Column<string>(maxLength: 160, nullable: false),
                    DisplayName = table.Column<string>(maxLength: 160, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QualityWorkflowAuditEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "QualityWorkflows",
                columns: table => new
                {
                    Id = table.Column<int>(type: ActiveProvider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) ? "INTEGER" : "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1").Annotation("Sqlite:Autoincrement", true),
                    Module = table.Column<string>(maxLength: 80, nullable: false),
                    DraftJson = table.Column<string>(type: ActiveProvider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) ? "TEXT" : "nvarchar(max)", nullable: false),
                    PublishedJson = table.Column<string>(type: ActiveProvider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) ? "TEXT" : "nvarchar(max)", nullable: true),
                    Version = table.Column<long>(nullable: false),
                    PublishedRevision = table.Column<int>(nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(nullable: true),
                    PublishedBy = table.Column<string>(maxLength: 160, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(nullable: false),
                    UpdatedBy = table.Column<string>(maxLength: 160, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QualityWorkflows", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_QualityWorkflowAuditEntries_Module_Id",
                table: "QualityWorkflowAuditEntries",
                columns: new[] { "Module", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_QualityWorkflows_Module",
                table: "QualityWorkflows",
                column: "Module",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "QualityWorkflowAuditEntries");

            migrationBuilder.DropTable(
                name: "QualityWorkflows");
        }
    }
}
