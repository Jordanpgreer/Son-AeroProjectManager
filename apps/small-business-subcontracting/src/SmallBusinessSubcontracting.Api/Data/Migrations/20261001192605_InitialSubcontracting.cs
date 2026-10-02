using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmallBusinessSubcontracting.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialSubcontracting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var sqlite = ActiveProvider == "Microsoft.EntityFrameworkCore.Sqlite";
            migrationBuilder.CreateTable(
                name: "BusinessSizeTags",
                columns: table => new
                {
                    Id = table.Column<int>(type: sqlite ? "INTEGER" : "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1").Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: sqlite ? "TEXT" : "nvarchar(80)", maxLength: 80, nullable: false),
                    NormalizedName = table.Column<string>(type: sqlite ? "TEXT" : "nvarchar(80)", maxLength: 80, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: sqlite ? "TEXT" : "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: sqlite ? "TEXT" : "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BusinessSizeTags", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Vendors",
                columns: table => new
                {
                    Id = table.Column<int>(type: sqlite ? "INTEGER" : "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1").Annotation("Sqlite:Autoincrement", true),
                    FulcrumId = table.Column<string>(type: sqlite ? "TEXT" : "nvarchar(80)", maxLength: 80, nullable: false),
                    Name = table.Column<string>(type: sqlite ? "TEXT" : "nvarchar(200)", maxLength: 200, nullable: false),
                    VendorCode = table.Column<string>(type: sqlite ? "TEXT" : "nvarchar(120)", maxLength: 120, nullable: true),
                    Active = table.Column<bool>(type: sqlite ? "INTEGER" : "bit", nullable: false),
                    Website = table.Column<string>(type: sqlite ? "TEXT" : "nvarchar(500)", maxLength: 500, nullable: true),
                    ContactsJson = table.Column<string>(type: sqlite ? "TEXT" : "nvarchar(max)", nullable: false),
                    LastSyncedAt = table.Column<DateTimeOffset>(type: sqlite ? "TEXT" : "datetimeoffset", nullable: false),
                    LastCertificationDate = table.Column<DateOnly>(type: sqlite ? "TEXT" : "date", nullable: true),
                    Version = table.Column<int>(type: sqlite ? "INTEGER" : "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Vendors", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VendorAuditEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: sqlite ? "INTEGER" : "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1").Annotation("Sqlite:Autoincrement", true),
                    VendorId = table.Column<int>(type: sqlite ? "INTEGER" : "int", nullable: false),
                    Kind = table.Column<string>(type: sqlite ? "TEXT" : "nvarchar(80)", maxLength: 80, nullable: false),
                    Summary = table.Column<string>(type: sqlite ? "TEXT" : "nvarchar(500)", maxLength: 500, nullable: false),
                    Actor = table.Column<string>(type: sqlite ? "TEXT" : "nvarchar(160)", maxLength: 160, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: sqlite ? "TEXT" : "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VendorAuditEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VendorAuditEvents_Vendors_VendorId",
                        column: x => x.VendorId,
                        principalTable: "Vendors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VendorBusinessSizeTags",
                columns: table => new
                {
                    VendorId = table.Column<int>(type: sqlite ? "INTEGER" : "int", nullable: false),
                    BusinessSizeTagId = table.Column<int>(type: sqlite ? "INTEGER" : "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VendorBusinessSizeTags", x => new { x.VendorId, x.BusinessSizeTagId });
                    table.ForeignKey(
                        name: "FK_VendorBusinessSizeTags_BusinessSizeTags_BusinessSizeTagId",
                        column: x => x.BusinessSizeTagId,
                        principalTable: "BusinessSizeTags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_VendorBusinessSizeTags_Vendors_VendorId",
                        column: x => x.VendorId,
                        principalTable: "Vendors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VendorDocuments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: sqlite ? "TEXT" : "uniqueidentifier", nullable: false),
                    VendorId = table.Column<int>(type: sqlite ? "INTEGER" : "int", nullable: false),
                    OriginalFileName = table.Column<string>(type: sqlite ? "TEXT" : "nvarchar(255)", maxLength: 255, nullable: false),
                    RelativePath = table.Column<string>(type: sqlite ? "TEXT" : "nvarchar(1000)", maxLength: 1000, nullable: false),
                    ContentType = table.Column<string>(type: sqlite ? "TEXT" : "nvarchar(160)", maxLength: 160, nullable: false),
                    FileSize = table.Column<long>(type: sqlite ? "INTEGER" : "bigint", nullable: false),
                    FileHash = table.Column<string>(type: sqlite ? "TEXT" : "nvarchar(64)", maxLength: 64, nullable: false),
                    DocumentType = table.Column<string>(type: sqlite ? "TEXT" : "nvarchar(100)", maxLength: 100, nullable: false),
                    DocumentDate = table.Column<DateOnly>(type: sqlite ? "TEXT" : "date", nullable: false),
                    Notes = table.Column<string>(type: sqlite ? "TEXT" : "nvarchar(1000)", maxLength: 1000, nullable: true),
                    UploadedBy = table.Column<string>(type: sqlite ? "TEXT" : "nvarchar(160)", maxLength: 160, nullable: false),
                    UploadedAt = table.Column<DateTimeOffset>(type: sqlite ? "TEXT" : "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VendorDocuments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VendorDocuments_Vendors_VendorId",
                        column: x => x.VendorId,
                        principalTable: "Vendors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BusinessSizeTags_NormalizedName",
                table: "BusinessSizeTags",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VendorAuditEvents_VendorId",
                table: "VendorAuditEvents",
                column: "VendorId");

            migrationBuilder.CreateIndex(
                name: "IX_VendorBusinessSizeTags_BusinessSizeTagId",
                table: "VendorBusinessSizeTags",
                column: "BusinessSizeTagId");

            migrationBuilder.CreateIndex(
                name: "IX_VendorDocuments_VendorId",
                table: "VendorDocuments",
                column: "VendorId");

            migrationBuilder.CreateIndex(
                name: "IX_Vendors_FulcrumId",
                table: "Vendors",
                column: "FulcrumId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Vendors_Name",
                table: "Vendors",
                column: "Name");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VendorAuditEvents");

            migrationBuilder.DropTable(
                name: "VendorBusinessSizeTags");

            migrationBuilder.DropTable(
                name: "VendorDocuments");

            migrationBuilder.DropTable(
                name: "BusinessSizeTags");

            migrationBuilder.DropTable(
                name: "Vendors");
        }
    }
}
