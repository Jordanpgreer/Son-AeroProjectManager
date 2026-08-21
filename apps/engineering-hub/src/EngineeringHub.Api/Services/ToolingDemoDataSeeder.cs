using EngineeringHub.Api.Data;
using EngineeringHub.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace EngineeringHub.Api.Services;

public sealed class ToolingDemoDataSeeder(EngineeringDbContext db)
{
    private const string Actor = "Engineering demo seed";

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        if (await db.Tools.AnyAsync(cancellationToken))
        {
            var demoTools = await db.Tools.Include(tool => tool.PartNumbers)
                .Where(tool => tool.CreatedBy == Actor && tool.PartNumbers.Count == 0)
                .ToListAsync(cancellationToken);
            foreach (var tool in demoTools)
            {
                var partNumber = $"PN-{tool.ToolNumber.Replace("TL-", string.Empty, StringComparison.OrdinalIgnoreCase)}";
                tool.PartNumbers.Add(new ToolPartNumber
                {
                    Tool = tool,
                    PartNumber = partNumber,
                    NormalizedPartNumber = Normalize(partNumber)
                });
            }
            if (demoTools.Count > 0) await db.SaveChangesAsync(cancellationToken);
            return;
        }
        var now = DateTime.UtcNow;
        var locations = new[]
        {
            new ToolLocation { Code = "A001-002", NormalizedCode = "A001002", Description = "Tool crib aisle A, rack 001, bin 002", CreatedBy = Actor, CreatedAt = now.AddMonths(-8) },
            new ToolLocation { Code = "A001-003", NormalizedCode = "A001003", Description = "Tool crib aisle A, rack 001, bin 003", CreatedBy = Actor, CreatedAt = now.AddMonths(-8) },
            new ToolLocation { Code = "QA-HOLD-01", NormalizedCode = "QAHOLD01", Description = "Quality inspection hold area", CreatedBy = Actor, CreatedAt = now.AddMonths(-8) },
            new ToolLocation { Code = "CELL-04", NormalizedCode = "CELL04", Description = "Production cell 4 tooling point", CreatedBy = Actor, CreatedAt = now.AddMonths(-8) }
        };
        db.ToolLocations.AddRange(locations);

        var tools = new[]
        {
            Create("TL-204", "Housing Fixture Set", "Machining fixture", "Northwind Aerostructures", locations[0], now.AddMonths(-2), "Fixture set used for actuator housing milling and inspection."),
            Create("TL-319", "Seal Carrier Trim Tool", "Trim tool", "Helios Defense Systems", locations[1], now.AddMonths(-14), "Trim tool for carrier edge cleanup after cure."),
            Create("TL-411", "Gasket Profile Inspection Plate", "Inspection fixture", "Son-Aero", locations[0], now.AddMonths(-5), "Profile verification plate retained in the central tool crib."),
            Create("TL-530", "Bonded Insert Drill Jig", "Drill jig", "Orion Flight Systems", locations[2], null, "Development drill jig awaiting its first formal tooling audit."),
            Create("TL-672", "Pressure Seal Test Fixture", "Test fixture", "Son-Aero", locations[3], now.AddMonths(-1), "Pressure test fixture assigned to production cell 4.")
        };

        var vendorCheckoutAt = now.AddDays(-4);
        tools[1].CustodyStatus = ToolCustodyStatus.OutsideProcessing;
        tools[1].CurrentLocation = null;
        tools[1].CurrentVendor = "Precision Surface Processing";
        tools[1].CurrentHolder = "Vendor quality desk";
        tools[1].CheckedOutAt = vendorCheckoutAt;
        tools[1].Movements.Add(new ToolMovement
        {
            Type = ToolMovementType.SentToVendor,
            Vendor = tools[1].CurrentVendor,
            Person = tools[1].CurrentHolder,
            Purpose = "Protective coating renewal",
            InspectionConfirmed = true,
            InspectionNotes = "Visual condition accepted before shipment.",
            SignedOffBy = Actor,
            RecordedAt = vendorCheckoutAt
        });
        tools[1].AuditEntries.Add(new ToolAuditEntry
        {
            Tool = tools[1], Action = "ToolReleased", Details = "Demo release to Precision Surface Processing after inspection sign-off.", Actor = Actor, OccurredAt = vendorCheckoutAt
        });

        db.Tools.AddRange(tools);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static ToolRecord Create(
        string number,
        string name,
        string type,
        string owner,
        ToolLocation location,
        DateTime? auditDate,
        string notes)
    {
        var created = DateTime.UtcNow.AddMonths(-7);
        var tool = new ToolRecord
        {
            ToolNumber = number,
            NormalizedToolNumber = Normalize(number),
            Name = name,
            ToolType = type,
            Owner = owner,
            Notes = notes,
            HomeLocationAssignment = new ToolHomeLocation { Location = location },
            CurrentLocation = location,
            LastAuditDate = auditDate,
            LastAuditBy = auditDate.HasValue ? Actor : null,
            CreatedBy = Actor,
            CreatedAt = created,
            UpdatedBy = Actor,
            UpdatedAt = created
        };
        var partNumber = $"PN-{number.Replace("TL-", string.Empty, StringComparison.OrdinalIgnoreCase)}";
        tool.PartNumbers.Add(new ToolPartNumber
        {
            Tool = tool,
            PartNumber = partNumber,
            NormalizedPartNumber = Normalize(partNumber)
        });
        tool.Movements.Add(new ToolMovement
        {
            Type = ToolMovementType.Registered,
            Location = location,
            LocationCode = location.Code,
            Person = Actor,
            Purpose = "Demo tool registration",
            SignedOffBy = Actor,
            RecordedAt = created
        });
        tool.AuditEntries.Add(new ToolAuditEntry
        {
            Tool = tool, Action = "ToolCreated", Details = $"Created demonstration tool in {location.Code}.", Actor = Actor, OccurredAt = created
        });
        return tool;
    }

    private static string Normalize(string value) => string.Concat(value.ToUpperInvariant().Where(char.IsLetterOrDigit));
}
