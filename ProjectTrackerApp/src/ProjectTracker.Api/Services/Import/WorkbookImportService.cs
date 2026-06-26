using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Dtos;
using ProjectTracker.Api.Models;

namespace ProjectTracker.Api.Services.Import;

public sealed class WorkbookImportService(ProjectMetricsService metricsService)
{
    private static readonly XNamespace SpreadsheetNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace RelationshipNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRelationshipNs = "http://schemas.openxmlformats.org/package/2006/relationships";

    public async Task<ImportWorkbookResult> ImportAsync(
        ProjectTrackerDbContext db,
        string workbookPath,
        bool replaceExisting,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(workbookPath))
        {
            throw new FileNotFoundException("Workbook was not found.", workbookPath);
        }

        using var workbook = SpreadsheetWorkbook.Open(workbookPath);

        if (replaceExisting)
        {
            db.StatusHistory.RemoveRange(db.StatusHistory);
            db.Tasks.RemoveRange(db.Tasks);
            db.Projects.RemoveRange(db.Projects);
            db.Phases.RemoveRange(db.Phases);
            db.Holidays.RemoveRange(db.Holidays);
            await db.SaveChangesAsync(cancellationToken);
        }

        var holidays = ImportHolidays(db, workbook);
        var phaseNames = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var projectCount = 0;
        var taskCount = 0;

        foreach (var sheet in workbook.Sheets)
        {
            if (sheet.Name is "Program Dashboard" or "Template" or "Holiday Schedule")
            {
                continue;
            }

            var rows = workbook.ReadRows(sheet);
            var rawProgramName = rows.GetValue(2, "B")?.Trim();
            var programName = string.IsNullOrWhiteSpace(rawProgramName)
                || string.Equals(rawProgramName, "Part Number Here", StringComparison.OrdinalIgnoreCase)
                    ? sheet.Name
                    : rawProgramName;
            var tasks = ImportTasks(rows, phaseNames);
            if (tasks.Count == 0)
            {
                continue;
            }

            // Only add programs that don't already exist — never delete or override an existing one.
            var alreadyExists = await db.Projects.AnyAsync(project => project.ProgramName == programName, cancellationToken);
            if (alreadyExists)
            {
                continue;
            }

            var project = new Project
            {
                ProgramName = programName,
                ProgramManager = "Josh Greer"
            };

            foreach (var task in tasks)
            {
                project.Tasks.Add(task);
            }

            metricsService.RefreshProject(project, holidays.Select(holiday => holiday.Date).ToHashSet(), DateOnly.FromDateTime(DateTime.Today));
            db.Projects.Add(project);
            projectCount++;
            taskCount += tasks.Count;
        }

        var sortOrder = 10;
        foreach (var phaseName in phaseNames)
        {
            if (!await db.Phases.AnyAsync(phase => phase.Name == phaseName, cancellationToken))
            {
                db.Phases.Add(new Phase { Name = phaseName, SortOrder = sortOrder });
                sortOrder += 10;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return new ImportWorkbookResult(projectCount, taskCount, holidays.Count);
    }

    private static List<Holiday> ImportHolidays(ProjectTrackerDbContext db, SpreadsheetWorkbook workbook)
    {
        var sheet = workbook.Sheets.FirstOrDefault(sheet => sheet.Name == "Holiday Schedule");
        if (sheet is null)
        {
            return [];
        }

        var rows = workbook.ReadRows(sheet);
        var seenDates = db.Holidays.Select(holiday => holiday.Date).ToHashSet();
        var holidays = new List<Holiday>();
        for (var row = 1; row <= 200; row++)
        {
            var date = ParseExcelDate(rows.GetValue(row, "C"));
            // Skip blanks and any date already present (so appending a workbook never duplicates holidays).
            if (date is null || !seenDates.Add(date.Value))
            {
                continue;
            }

            var holiday = new Holiday
            {
                Date = date.Value,
                Name = $"Company holiday {date.Value:yyyy-MM-dd}"
            };
            db.Holidays.Add(holiday);
            holidays.Add(holiday);
        }

        return holidays;
    }

    private static List<ProjectTask> ImportTasks(SheetRows rows, ISet<string> phaseNames)
    {
        var tasks = new List<ProjectTask>();
        var sequence = 10;

        for (var row = 9; row <= 200; row++)
        {
            var title = rows.GetValue(row, "C")?.Trim();
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var phase = rows.GetValue(row, "D")?.Trim();
            if (!string.IsNullOrWhiteSpace(phase))
            {
                phaseNames.Add(phase);
            }

            tasks.Add(new ProjectTask
            {
                Sequence = sequence,
                ExternalTaskId = rows.GetValue(row, "B"),
                Title = title,
                Phase = string.IsNullOrWhiteSpace(phase) ? null : phase,
                StartDate = ParseExcelDate(rows.GetValue(row, "E")),
                OriginalStartDate = ParseExcelDate(rows.GetValue(row, "F")),
                EndDate = ParseExcelDate(rows.GetValue(row, "G")),
                OriginalEndDate = ParseExcelDate(rows.GetValue(row, "H")),
                EstimatedDuration = ParseInteger(rows.GetValue(row, "I")),
                ActualDuration = ParseInteger(rows.GetValue(row, "J")),
                PercentComplete = ParseDecimal(rows.GetValue(row, "K")) ?? 0m,
                Status = ParseStatus(rows.GetValue(row, "L")),
                Notes = rows.GetValue(row, "M")
            });

            sequence += 10;
        }

        return tasks;
    }

    private static DateOnly? ParseExcelDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var serial) && serial > 0)
        {
            return DateOnly.FromDateTime(DateTime.FromOADate(serial));
        }

        return DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : null;
    }

    private static int? ParseInteger(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var decimalValue))
        {
            return (int)Math.Round(decimalValue, MidpointRounding.AwayFromZero);
        }

        return null;
    }

    private static decimal? ParseDecimal(string? value)
    {
        return decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private static TaskScheduleStatus ParseStatus(string? value)
    {
        return value?.Trim() switch
        {
            "Complete" => TaskScheduleStatus.Complete,
            "Behind" => TaskScheduleStatus.Behind,
            "On Track" => TaskScheduleStatus.OnTrack,
            "Not Started" => TaskScheduleStatus.NotStarted,
            _ => TaskScheduleStatus.NotStarted
        };
    }

    private sealed record WorkbookSheet(string Name, string Path);

    private sealed class SheetRows(Dictionary<int, Dictionary<string, string?>> values)
    {
        public string? GetValue(int row, string column)
        {
            return values.TryGetValue(row, out var rowValues) && rowValues.TryGetValue(column, out var value)
                ? value
                : null;
        }
    }

    private sealed class SpreadsheetWorkbook : IDisposable
    {
        private readonly ZipArchive archive;
        private readonly List<string> sharedStrings;

        private SpreadsheetWorkbook(ZipArchive archive, List<WorkbookSheet> sheets, List<string> sharedStrings)
        {
            this.archive = archive;
            Sheets = sheets;
            this.sharedStrings = sharedStrings;
        }

        public IReadOnlyList<WorkbookSheet> Sheets { get; }

        public static SpreadsheetWorkbook Open(string path)
        {
            var archive = ZipFile.OpenRead(path);
            var sharedStrings = ReadSharedStrings(archive);
            var workbookDoc = ReadXml(archive, "xl/workbook.xml");
            var relsDoc = ReadXml(archive, "xl/_rels/workbook.xml.rels");
            var relationships = relsDoc.Root!
                .Elements(PackageRelationshipNs + "Relationship")
                .ToDictionary(
                    relationship => relationship.Attribute("Id")!.Value,
                    relationship => relationship.Attribute("Target")!.Value);

            var sheets = workbookDoc.Root!
                .Element(SpreadsheetNs + "sheets")!
                .Elements(SpreadsheetNs + "sheet")
                .Select(sheet =>
                {
                    var name = sheet.Attribute("name")!.Value;
                    var relationshipId = sheet.Attribute(RelationshipNs + "id")!.Value;
                    var target = relationships[relationshipId];
                    var normalized = target.StartsWith("xl/", StringComparison.OrdinalIgnoreCase)
                        ? target
                        : $"xl/{target.TrimStart('/')}";
                    return new WorkbookSheet(name, normalized.Replace('\\', '/'));
                })
                .ToList();

            return new SpreadsheetWorkbook(archive, sheets, sharedStrings);
        }

        public SheetRows ReadRows(WorkbookSheet sheet)
        {
            var doc = ReadXml(archive, sheet.Path);
            var rows = new Dictionary<int, Dictionary<string, string?>>();
            foreach (var row in doc.Descendants(SpreadsheetNs + "row"))
            {
                if (!int.TryParse(row.Attribute("r")?.Value, out var rowIndex))
                {
                    continue;
                }

                var rowValues = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                foreach (var cell in row.Elements(SpreadsheetNs + "c"))
                {
                    var reference = cell.Attribute("r")?.Value;
                    var column = GetColumn(reference);
                    if (column is null)
                    {
                        continue;
                    }

                    rowValues[column] = ReadCellValue(cell);
                }

                rows[rowIndex] = rowValues;
            }

            return new SheetRows(rows);
        }

        public void Dispose() => archive.Dispose();

        private string? ReadCellValue(XElement cell)
        {
            var type = cell.Attribute("t")?.Value;
            if (type == "inlineStr")
            {
                return string.Concat(cell.Descendants(SpreadsheetNs + "t").Select(node => node.Value));
            }

            var value = cell.Element(SpreadsheetNs + "v")?.Value;
            if (value is null)
            {
                return null;
            }

            if (type == "s" && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sharedStringIndex))
            {
                return sharedStringIndex >= 0 && sharedStringIndex < sharedStrings.Count ? sharedStrings[sharedStringIndex] : value;
            }

            return value;
        }

        private static string? GetColumn(string? reference)
        {
            if (string.IsNullOrWhiteSpace(reference))
            {
                return null;
            }

            var letters = new string(reference.TakeWhile(char.IsLetter).ToArray());
            return string.IsNullOrWhiteSpace(letters) ? null : letters;
        }

        private static List<string> ReadSharedStrings(ZipArchive archive)
        {
            var entry = archive.GetEntry("xl/sharedStrings.xml");
            if (entry is null)
            {
                return [];
            }

            using var stream = entry.Open();
            var doc = XDocument.Load(stream);
            return doc.Root!
                .Elements(SpreadsheetNs + "si")
                .Select(si => string.Concat(si.Descendants(SpreadsheetNs + "t").Select(text => text.Value)))
                .ToList();
        }

        private static XDocument ReadXml(ZipArchive archive, string path)
        {
            var entry = archive.GetEntry(path) ?? throw new InvalidDataException($"Missing workbook entry: {path}");
            using var stream = entry.Open();
            return XDocument.Load(stream);
        }
    }
}
