using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QualityAssurance.Api.Data.Migrations
{
    [DbContext(typeof(QualityAssuranceDbContext))]
    [Migration("20260813181727_InitialQualityShipping")]
    /// <inheritdoc />
    public partial class InitialQualityShipping : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "QualityAssignmentRules",
                columns: table => new
                {
                    Id = table.Column<int>(nullable: false)
                        .Annotation("Sqlite:Autoincrement", true)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(maxLength: 160, nullable: false),
                    IsEnabled = table.Column<bool>(nullable: false),
                    Priority = table.Column<int>(nullable: false),
                    MatchField = table.Column<string>(maxLength: 40, nullable: false),
                    MatchOperator = table.Column<string>(maxLength: 40, nullable: false),
                    MatchValue = table.Column<string>(maxLength: 240, nullable: false),
                    TargetGroupId = table.Column<int>(nullable: false),
                    TargetGroupName = table.Column<string>(maxLength: 160, nullable: false),
                    AssignmentMode = table.Column<string>(maxLength: 40, nullable: false),
                    TargetUserId = table.Column<int>(nullable: true),
                    TargetAccountName = table.Column<string>(maxLength: 160, nullable: true),
                    TargetDisplayName = table.Column<string>(maxLength: 160, nullable: true),
                    Version = table.Column<long>(nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(nullable: false),
                    CreatedBy = table.Column<string>(nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(nullable: false),
                    UpdatedBy = table.Column<string>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QualityAssignmentRules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "QualityShipments",
                columns: table => new
                {
                    Id = table.Column<int>(nullable: false)
                        .Annotation("Sqlite:Autoincrement", true)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Status = table.Column<string>(maxLength: 80, nullable: false),
                    SalesOrderNumber = table.Column<string>(maxLength: 80, nullable: false),
                    QaArrivalDate = table.Column<DateOnly>(nullable: true),
                    PartNumber = table.Column<string>(maxLength: 160, nullable: false),
                    PurchaseOrderNumber = table.Column<string>(maxLength: 160, nullable: true),
                    Customer = table.Column<string>(maxLength: 240, nullable: false),
                    TaskType = table.Column<string>(maxLength: 120, nullable: false),
                    Quantity = table.Column<decimal>(precision: 18, scale: 3, nullable: true),
                    DollarValue = table.Column<decimal>(precision: 18, scale: 2, nullable: true),
                    ShipDate = table.Column<DateOnly>(nullable: true),
                    HoldReason = table.Column<string>(nullable: true),
                    SourceRequestedDate = table.Column<DateOnly>(nullable: true),
                    NextAction = table.Column<string>(nullable: true),
                    Comments = table.Column<string>(nullable: true),
                    LastWorkedAt = table.Column<DateTimeOffset>(nullable: true),
                    AssignedGroupId = table.Column<int>(nullable: true),
                    AssignedGroupName = table.Column<string>(maxLength: 160, nullable: true),
                    AssignedUserId = table.Column<int>(nullable: true),
                    AssignedAccountName = table.Column<string>(maxLength: 160, nullable: true),
                    AssignedDisplayName = table.Column<string>(maxLength: 160, nullable: true),
                    IsShipped = table.Column<bool>(nullable: false),
                    ShippedAt = table.Column<DateTimeOffset>(nullable: true),
                    ShippedByAccountName = table.Column<string>(nullable: true),
                    ShippedByDisplayName = table.Column<string>(nullable: true),
                    Version = table.Column<long>(nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(nullable: false),
                    CreatedByAccountName = table.Column<string>(nullable: false),
                    CreatedByDisplayName = table.Column<string>(nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(nullable: false),
                    UpdatedByAccountName = table.Column<string>(nullable: false),
                    UpdatedByDisplayName = table.Column<string>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QualityShipments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "QualityShipmentAuditEntries",
                columns: table => new
                {
                    Id = table.Column<long>(nullable: false)
                        .Annotation("Sqlite:Autoincrement", true)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ShipmentId = table.Column<int>(nullable: false),
                    EventType = table.Column<string>(maxLength: 80, nullable: false),
                    FieldName = table.Column<string>(maxLength: 120, nullable: true),
                    OldValue = table.Column<string>(nullable: true),
                    NewValue = table.Column<string>(nullable: true),
                    AccountName = table.Column<string>(maxLength: 160, nullable: false),
                    DisplayName = table.Column<string>(maxLength: 160, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QualityShipmentAuditEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QualityShipmentAuditEntries_QualityShipments_ShipmentId",
                        column: x => x.ShipmentId,
                        principalTable: "QualityShipments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_QualityAssignmentRules_IsEnabled_Priority",
                table: "QualityAssignmentRules",
                columns: new[] { "IsEnabled", "Priority" });

            migrationBuilder.CreateIndex(
                name: "IX_QualityShipmentAuditEntries_ShipmentId_OccurredAt",
                table: "QualityShipmentAuditEntries",
                columns: new[] { "ShipmentId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_QualityShipments_Customer",
                table: "QualityShipments",
                column: "Customer");

            migrationBuilder.CreateIndex(
                name: "IX_QualityShipments_IsShipped_AssignedGroupId_ShipDate",
                table: "QualityShipments",
                columns: new[] { "IsShipped", "AssignedGroupId", "ShipDate" });

            migrationBuilder.CreateIndex(
                name: "IX_QualityShipments_IsShipped_AssignedUserId_CreatedAt",
                table: "QualityShipments",
                columns: new[] { "IsShipped", "AssignedUserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_QualityShipments_TaskType",
                table: "QualityShipments",
                column: "TaskType");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "QualityAssignmentRules");

            migrationBuilder.DropTable(
                name: "QualityShipmentAuditEntries");

            migrationBuilder.DropTable(
                name: "QualityShipments");
        }
    }
}
