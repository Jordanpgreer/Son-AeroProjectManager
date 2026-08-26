using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QualityAssurance.Api.Data.Migrations
{
    [DbContext(typeof(QualityAssuranceDbContext))]
    [Migration("20260813190849_AddQualityShippingLayoutPreferences")]
    /// <inheritdoc />
    public partial class AddQualityShippingLayoutPreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "QualityShippingLayoutPreferences",
                columns: table => new
                {
                    Id = table.Column<int>(nullable: false)
                        .Annotation("Sqlite:Autoincrement", true)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AppUserId = table.Column<int>(nullable: false),
                    AccountName = table.Column<string>(maxLength: 160, nullable: false),
                    LayoutJson = table.Column<string>(maxLength: 12000, nullable: false),
                    Version = table.Column<long>(nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QualityShippingLayoutPreferences", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_QualityShippingLayoutPreferences_AppUserId",
                table: "QualityShippingLayoutPreferences",
                column: "AppUserId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "QualityShippingLayoutPreferences");
        }
    }
}
