using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QualityAssurance.Api.Data.Migrations
{
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
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    MatchField = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    MatchOperator = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    MatchValue = table.Column<string>(type: "TEXT", maxLength: 240, nullable: false),
                    TargetGroupId = table.Column<int>(type: "INTEGER", nullable: false),
                    TargetGroupName = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    AssignmentMode = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    TargetUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    TargetAccountName = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    TargetDisplayName = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QualityAssignmentRules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "QualityShipments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    SalesOrderNumber = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    QaArrivalDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    PartNumber = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    PurchaseOrderNumber = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    Customer = table.Column<string>(type: "TEXT", maxLength: 240, nullable: false),
                    TaskType = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Quantity = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: true),
                    DollarValue = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: true),
                    ShipDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    HoldReason = table.Column<string>(type: "TEXT", nullable: true),
                    SourceRequestedDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    NextAction = table.Column<string>(type: "TEXT", nullable: true),
                    Comments = table.Column<string>(type: "TEXT", nullable: true),
                    LastWorkedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    AssignedGroupId = table.Column<int>(type: "INTEGER", nullable: true),
                    AssignedGroupName = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    AssignedUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    AssignedAccountName = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    AssignedDisplayName = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    IsShipped = table.Column<bool>(type: "INTEGER", nullable: false),
                    ShippedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ShippedByAccountName = table.Column<string>(type: "TEXT", nullable: true),
                    ShippedByDisplayName = table.Column<string>(type: "TEXT", nullable: true),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedByAccountName = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedByDisplayName = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedByAccountName = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedByDisplayName = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QualityShipments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "QualityShipmentAuditEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ShipmentId = table.Column<int>(type: "INTEGER", nullable: false),
                    EventType = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    FieldName = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    OldValue = table.Column<string>(type: "TEXT", nullable: true),
                    NewValue = table.Column<string>(type: "TEXT", nullable: true),
                    AccountName = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
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
