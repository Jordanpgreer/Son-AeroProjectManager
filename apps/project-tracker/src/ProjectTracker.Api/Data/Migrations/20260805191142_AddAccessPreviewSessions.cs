using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProjectTracker.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAccessPreviewSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AccessPreviewSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TokenHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    AdministratorAccountName = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    TargetKey = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    ApplicationId = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    IssuedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LaunchExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SessionExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RedeemedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessPreviewSessions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccessPreviewSessions_ApplicationId_SessionExpiresAt",
                table: "AccessPreviewSessions",
                columns: new[] { "ApplicationId", "SessionExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AccessPreviewSessions_TokenHash",
                table: "AccessPreviewSessions",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccessPreviewSessions");
        }
    }
}
