using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;

#nullable disable

namespace ProjectTracker.Api.Data.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(ProjectTrackerDbContext))]
    [Migration("20260626133000_AddProjectCustomerSalesOrder")]
    public partial class AddProjectCustomerSalesOrder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CustomerName",
                table: "Projects",
                type: "nvarchar(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SalesOrderNumber",
                table: "Projects",
                type: "nvarchar(80)",
                maxLength: 80,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CustomerName",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "SalesOrderNumber",
                table: "Projects");
        }
    }
}
