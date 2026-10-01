using ClosedXML.Excel;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using SmallBusinessSubcontracting.Api;

namespace SmallBusinessSubcontracting.Tests;

public sealed class VendorServiceTests
{
    [Fact]
    public async Task Synchronize_updates_Fulcrum_fields_without_replacing_compliance_data()
    {
        await using var fixture = await VendorFixture.CreateAsync([
            new FulcrumVendorSnapshot(
                "123456789012345678901234",
                "Updated Vendor Name",
                "VEN-001",
                true,
                "https://vendor.example",
                [new FulcrumVendorContactSnapshot("contact-1", "Jane Doe", "Sales", "555-0100", "jane@example.com")])
        ]);
        var vendor = new VendorRecord
        {
            FulcrumId = "123456789012345678901234",
            Name = "Old Vendor Name",
            Active = true,
            LastSyncedAt = DateTimeOffset.Parse("2025-01-01T00:00:00Z"),
            LastCertificationDate = new DateOnly(2026, 1, 15),
            BusinessSizes =
            [
                new VendorBusinessSizeTag
                {
                    BusinessSizeTag = new BusinessSizeTag
                    {
                        Name = "Small Business",
                        NormalizedName = "SMALL BUSINESS",
                        CreatedAt = DateTimeOffset.UtcNow,
                        CreatedBy = "Tester"
                    }
                }
            ]
        };
        fixture.Db.Vendors.Add(vendor);
        await fixture.Db.SaveChangesAsync();

        var result = await fixture.Service.SynchronizeAsync("Admin User", CancellationToken.None);

        fixture.Db.ChangeTracker.Clear();
        var saved = await fixture.Db.Vendors
            .Include(record => record.BusinessSizes).ThenInclude(link => link.BusinessSizeTag)
            .Include(record => record.AuditEvents)
            .SingleAsync();
        Assert.Equal(1, result.Updated);
        Assert.Equal("Updated Vendor Name", saved.Name);
        Assert.Equal(new DateOnly(2026, 1, 15), saved.LastCertificationDate);
        Assert.Equal("Small Business", Assert.Single(saved.BusinessSizes).BusinessSizeTag.Name);
        Assert.Contains(saved.AuditEvents, audit => audit.Kind == "Fulcrum sync");
    }

    [Fact]
    public async Task Dashboard_search_and_export_use_business_size_filter()
    {
        await using var fixture = await VendorFixture.CreateAsync([]);
        fixture.Db.Vendors.AddRange(
            Vendor("Alpha Aerospace", "Small Business", new DateOnly(2026, 2, 1)),
            Vendor("Omega Supply", "Large Business", new DateOnly(2026, 3, 1)));
        await fixture.Db.SaveChangesAsync();

        var dashboard = await fixture.Service.GetDashboardAsync(
            "small",
            null,
            null,
            null,
            "name",
            "asc",
            CancellationToken.None);
        var workbookBytes = await fixture.Service.ExportDashboardAsync(
            null,
            "Small Business",
            null,
            null,
            "name",
            "asc",
            CancellationToken.None);

        Assert.Equal("Alpha Aerospace", Assert.Single(dashboard.Vendors).Name);
        using var stream = new MemoryStream(workbookBytes);
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheet("Vendor Compliance");
        Assert.Equal("Alpha Aerospace", sheet.Cell(2, 1).GetString());
        Assert.True(sheet.Cell(3, 1).IsEmpty());
    }

    private static VendorRecord Vendor(string name, string size, DateOnly certificationDate) => new()
    {
        FulcrumId = Guid.NewGuid().ToString("N")[..24],
        Name = name,
        Active = true,
        LastSyncedAt = DateTimeOffset.UtcNow,
        LastCertificationDate = certificationDate,
        BusinessSizes =
        [
            new VendorBusinessSizeTag
            {
                BusinessSizeTag = new BusinessSizeTag
                {
                    Name = size,
                    NormalizedName = size.ToUpperInvariant(),
                    CreatedAt = DateTimeOffset.UtcNow,
                    CreatedBy = "Tester"
                }
            }
        ]
    };

    private sealed class VendorFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly string documentRoot;

        private VendorFixture(
            SqliteConnection connection,
            string documentRoot,
            SubcontractingDbContext db,
            VendorService service)
        {
            this.connection = connection;
            this.documentRoot = documentRoot;
            Db = db;
            Service = service;
        }

        public SubcontractingDbContext Db { get; }
        public VendorService Service { get; }

        public static async Task<VendorFixture> CreateAsync(IReadOnlyList<FulcrumVendorSnapshot> snapshots)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<SubcontractingDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new SubcontractingDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var documentRoot = Path.Combine(Path.GetTempPath(), $"subcontracting-tests-{Guid.NewGuid():N}");
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["VendorDocumentStorage:RootPath"] = documentRoot,
                ["VendorDocumentStorage:RequireUncPath"] = "false"
            }).Build();
            var documents = new VendorDocumentStore(configuration, new TestEnvironment(documentRoot));
            var service = new VendorService(db, new StubFulcrumClient(snapshots), documents, TimeProvider.System);
            return new VendorFixture(connection, documentRoot, db, service);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
            if (Directory.Exists(documentRoot)) Directory.Delete(documentRoot, true);
        }
    }

    private sealed class StubFulcrumClient(IReadOnlyList<FulcrumVendorSnapshot> snapshots) : IFulcrumVendorClient
    {
        public Task<IReadOnlyList<FulcrumVendorSnapshot>> GetVendorsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(snapshots);
    }

    private sealed class TestEnvironment(string contentRoot) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "SmallBusinessSubcontracting.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = contentRoot;
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
