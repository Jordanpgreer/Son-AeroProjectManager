using EngineeringHub.Api.Data;
using EngineeringHub.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace EngineeringHub.Api.Services;

public sealed class EngineeringDemoDataSeeder(EngineeringDbContext db)
{
    private const string DemoActor = "Engineering demo seed";

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        if (await db.Drawings.AnyAsync(cancellationToken)) return;

        var now = DateTime.UtcNow;
        var drawings = new[]
        {
            CreateDrawing(
                "DRW-100014-A",
                "Actuator Housing Machining Drawing",
                "Northwind Aerostructures",
                ["PN-100014", "PN-100015"],
                "Machining and inspection definition for the actuator housing family.",
                "Mylar Cabinet A / Slot 14",
                ("Specification", "SPEC-4418", "Housing Assembly Process Specification"),
                ("WorkOrder", "WO-24771", "Actuator housing qualification build"),
                ("WorkInstruction", "WI-100014-MFG", "Housing machining work instruction")),
            CreateDrawing(
                "DRW-100287-B",
                "High-Temperature Seal Carrier",
                "Helios Defense Systems",
                ["PN-100287"],
                "Detail drawing with CMP-77 material callouts and finish verification notes.",
                "Mylar Cabinet B / Slot 08",
                ("Specification", "SPEC-5220", "Seal Carrier Product Specification"),
                ("WorkOrder", "WO-25102", "Seal carrier validation lot")),
            CreateDrawing(
                "DRW-100411",
                "Fluorosilicone Gasket Profile",
                "Skyreach Propulsion",
                ["PN-100411", "PN-100412"],
                "Profile definition for elevated-temperature gasket qualification.",
                null,
                ("Specification", "SPEC-6104", "Elastomer Qualification Specification"),
                ("SupplementalDocument", "DOC-100411-CALC", "Gasket compression calculation"),
                ("WorkOrder", "WO-25498", "Thermal qualification build")),
            CreateDrawing(
                "DRW-100530",
                "Bonded Insert Assembly",
                "Orion Flight Systems",
                ["PN-100530"],
                "Assembly drawing prepared for initial engineering review.",
                "Mylar Cabinet C / Slot 03",
                ("Specification", "SPEC-7021", "Bonded Insert Assembly Specification"),
                ("WorkInstruction", "WI-100530-BOND", "Insert bonding work instruction")),
            CreateDrawing(
                "DRW-100672",
                "Pressure Seal Test Fixture",
                "Apex Spaceworks",
                ["PN-100672"],
                "New fixture drawing awaiting its first controlled drawing file upload.",
                null,
                ("WorkOrder", "WO-26018", "Pressure seal fixture development"))
        };

        AddMetadataRevision(drawings[0], "A", DrawingRevisionStatus.UnderReview, now.AddDays(-5), "Initial release package prepared for review.");
        AddMetadataRevision(drawings[1], "B", DrawingRevisionStatus.Draft, now.AddDays(-3), "Updated compound callout and finish note.");
        AddMetadataRevision(drawings[2], "A", DrawingRevisionStatus.Draft, now.AddDays(-2), "Initial profile definition.");
        AddMetadataRevision(drawings[3], "P1", DrawingRevisionStatus.UnderReview, now.AddDays(-1), "Preliminary assembly definition for review.");

        drawings[0].ApprovalStatus = DrawingApprovalStatus.UnderReview;
        drawings[3].ApprovalStatus = DrawingApprovalStatus.UnderReview;
        drawings[1].IsMylarCheckedOut = true;
        drawings[1].MylarCheckedOutBy = "Demo Engineer";
        drawings[1].MylarCheckedOutAt = now.AddHours(-6);
        drawings[1].Mylars[0].IsCheckedOut = true;
        drawings[1].Mylars[0].CheckedOutBy = "Demo Engineer";
        drawings[1].Mylars[0].CheckedOutAt = now.AddHours(-6);
        drawings[1].MylarTransactions.Add(new MylarTransaction
        {
            Mylar = drawings[1].Mylars[0],
            Type = MylarTransactionType.CheckedOut,
            Person = "Demo Engineer",
            Purpose = "Shop-floor verification",
            Location = drawings[1].PhysicalMylarLocation,
            RecordedBy = DemoActor,
            RecordedAt = now.AddHours(-6)
        });

        db.Drawings.AddRange(drawings);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static Drawing CreateDrawing(
        string number,
        string title,
        string customer,
        IReadOnlyList<string> partNumbers,
        string notes,
        string? mylarLocation,
        params (string Kind, string Reference, string Title)[] links)
    {
        var drawing = new Drawing
        {
            DrawingNumber = number,
            NormalizedDrawingNumber = Normalize(number),
            Title = title,
            Customer = customer,
            NormalizedCustomer = Normalize(customer),
            Notes = notes,
            PhysicalMylarLocation = mylarLocation,
            CreatedBy = DemoActor,
            CreatedAt = DateTime.UtcNow.AddDays(-10)
        };
        drawing.Parts.AddRange(partNumbers.Select(part => new DrawingPart { PartNumber = part }));
        drawing.DocumentLinks.AddRange(links.Select(link => new DrawingDocumentLink
        {
            Kind = Enum.Parse<DrawingDocumentKind>(link.Kind),
            ReferenceNumber = link.Reference,
            Title = link.Title
        }));
        if (!string.IsNullOrWhiteSpace(mylarLocation))
        {
            var mylar = new DrawingMylar
            {
                MylarNumber = "MYLAR-1",
                NormalizedMylarNumber = "MYLAR1",
                CurrentLocation = mylarLocation,
                CreatedBy = DemoActor,
                CreatedAt = drawing.CreatedAt
            };
            drawing.Mylars.Add(mylar);
            drawing.MylarTransactions.Add(new MylarTransaction
            {
                Mylar = mylar,
                Type = MylarTransactionType.Registered,
                Person = DemoActor,
                Purpose = "Demo Mylar registered.",
                Location = mylarLocation,
                RecordedBy = DemoActor,
                RecordedAt = drawing.CreatedAt
            });
        }
        drawing.AuditEntries.Add(new DrawingAuditEntry
        {
            Drawing = drawing,
            Action = "DemoDrawingCreated",
            Details = "Created metadata-only demonstration drawing. No controlled drawing file was generated.",
            Actor = DemoActor,
            OccurredAt = drawing.CreatedAt
        });
        return drawing;
    }

    private static void AddMetadataRevision(
        Drawing drawing,
        string revisionNumber,
        DrawingRevisionStatus status,
        DateTime uploadedAt,
        string description)
    {
        drawing.Revisions.Add(new DrawingRevision
        {
            RevisionNumber = revisionNumber,
            RevisionDate = uploadedAt.Date,
            UploadedAt = uploadedAt,
            ChangeDescription = description,
            Status = status,
            OriginalFileName = "Drawing file not uploaded - demo metadata only",
            StoredFilePath = string.Empty,
            FileType = "application/pdf",
            FileSize = 0,
            FileHash = string.Empty,
            UploadedBy = DemoActor,
            Notes = "Demonstration record only. A real PDF is required before approval."
        });
        drawing.AuditEntries.Add(new DrawingAuditEntry
        {
            Drawing = drawing,
            RevisionNumber = revisionNumber,
            Action = "DemoRevisionCreated",
            Details = "Created metadata-only demonstration revision without a PDF.",
            Actor = DemoActor,
            OccurredAt = uploadedAt
        });
    }

    private static string Normalize(string value) =>
        string.Concat(value.Trim().ToUpperInvariant().Where(char.IsLetterOrDigit));
}
