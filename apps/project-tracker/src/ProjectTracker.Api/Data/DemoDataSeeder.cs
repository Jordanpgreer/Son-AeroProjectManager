using Microsoft.EntityFrameworkCore;
using ProjectTracker.Api.Models;
using ProjectTracker.Api.Services;

namespace ProjectTracker.Api.Data;

public static class DemoDataSeeder
{
    private const string DemoAdmin = @"DEV\ProjectTrackerAdmin";
    private const string AlexAccount = @"SON-AERO\alex.morgan";
    private const string CaseyAccount = @"SON-AERO\casey.lee";

    public static async Task SeedAsync(
        ProjectTrackerDbContext db,
        MentionNotificationService notifications,
        CancellationToken cancellationToken = default)
    {
        var users = await EnsureDemoUsersAsync(db, cancellationToken);
        var newMentionMessages = new List<ProjectMessage>();

        await EnsureActiveProjectAsync(
            db,
            "Test 5",
            "Demo Aerospace Customer C",
            "SO-TEST-1005",
            "JOB-TEST-5005",
            "Alex Morgan",
            "Casey Lee",
            new DateOnly(2026, 7, 20),
            BuildTest5Tasks(),
            [
                ("Alex Morgan", AlexAccount, "Long-lead material is released. @ProjectTrackerAdmin the October delivery plan is ready for review.", new DateTimeOffset(2026, 7, 21, 14, 15, 0, TimeSpan.Zero)),
                ("Casey Lee", CaseyAccount, "CNC capacity overlaps Test 6 in August. We will review the shared mill loading in the production meeting.", new DateTimeOffset(2026, 7, 22, 16, 40, 0, TimeSpan.Zero)),
                ("Alex Morgan", AlexAccount, "Customer confirmed the October 16 target remains acceptable.", new DateTimeOffset(2026, 7, 23, 13, 5, 0, TimeSpan.Zero))
            ],
            newMentionMessages,
            cancellationToken);

        await EnsureActiveProjectAsync(
            db,
            "Test 6",
            "Demo Aerospace Customer D",
            "SO-TEST-1006",
            "JOB-TEST-6006",
            "Casey Lee",
            "Alex Morgan",
            new DateOnly(2026, 7, 27),
            BuildTest6Tasks(),
            [
                ("Casey Lee", CaseyAccount, "@ProjectTrackerAdmin Test 6 now carries the second October schedule and the planned CNC conflict.", new DateTimeOffset(2026, 7, 22, 15, 0, 0, TimeSpan.Zero)),
                ("Alex Morgan", AlexAccount, "Programming is staged. The CNC Mill window overlaps Test 5 from August 10 through August 20.", new DateTimeOffset(2026, 7, 23, 12, 20, 0, TimeSpan.Zero)),
                ("Casey Lee", CaseyAccount, "Final inspection capacity is reserved for the week of October 26.", new DateTimeOffset(2026, 7, 23, 15, 45, 0, TimeSpan.Zero))
            ],
            newMentionMessages,
            cancellationToken);

        await EnsureCompletedProjectAsync(
            db,
            "Test 3",
            "Demo Aerospace Customer A",
            "SO-TEST-1003",
            "JOB-TEST-3003",
            new DateOnly(2026, 2, 2),
            new DateOnly(2026, 4, 23),
            new DateOnly(2026, 4, 20),
            "Completed three days ahead of the committed delivery.",
            cancellationToken);

        await EnsureCompletedProjectAsync(
            db,
            "Test 4",
            "Demo Aerospace Customer B",
            "SO-TEST-1004",
            "JOB-TEST-4004",
            new DateOnly(2026, 2, 16),
            new DateOnly(2026, 5, 14),
            new DateOnly(2026, 5, 19),
            "Completed five days after the committed delivery following outside processing.",
            cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        foreach (var message in newMentionMessages)
        {
            await notifications.AddForProjectMessageAsync(
                db,
                message,
                message.Project.ProgramName,
                message.AuthorAccountName,
                message.AuthorDisplayName,
                cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task<Dictionary<string, AppUser>> EnsureDemoUsersAsync(
        ProjectTrackerDbContext db,
        CancellationToken cancellationToken)
    {
        var groups = await db.Groups.ToDictionaryAsync(group => group.Name, StringComparer.OrdinalIgnoreCase, cancellationToken);
        var users = await db.Users
            .Include(user => user.GroupMemberships)
            .ToDictionaryAsync(user => user.AccountName, StringComparer.OrdinalIgnoreCase, cancellationToken);

        EnsureUser(db, users, AlexAccount, "Alex Morgan", groups.GetValueOrDefault("Engineering")?.Id);
        EnsureUser(db, users, CaseyAccount, "Casey Lee", groups.GetValueOrDefault("Managers")?.Id);
        await db.SaveChangesAsync(cancellationToken);
        return users;
    }

    private static void EnsureUser(
        ProjectTrackerDbContext db,
        IDictionary<string, AppUser> users,
        string account,
        string displayName,
        int? groupId)
    {
        if (!users.TryGetValue(account, out var user))
        {
            user = new AppUser
            {
                AccountName = account,
                DisplayName = displayName,
                IsActive = true,
                LastSeenAt = DateTimeOffset.UnixEpoch
            };
            db.Users.Add(user);
            users[account] = user;
        }

        if (groupId is not null && user.GroupMemberships.All(membership => membership.AppGroupId != groupId))
        {
            user.GroupMemberships.Add(new AppUserGroupMembership { AppGroupId = groupId.Value });
        }
    }

    private static async Task EnsureActiveProjectAsync(
        ProjectTrackerDbContext db,
        string name,
        string customer,
        string salesOrder,
        string jobNumber,
        string manager,
        string engineer,
        DateOnly programStart,
        IReadOnlyList<ProjectTask> tasks,
        IReadOnlyList<(string Author, string Account, string Body, DateTimeOffset At)> messages,
        ICollection<ProjectMessage> newMentionMessages,
        CancellationToken cancellationToken)
    {
        var project = await db.Projects
            .Include(candidate => candidate.Tasks)
            .Include(candidate => candidate.Messages)
            .FirstOrDefaultAsync(candidate => candidate.ProgramName == name, cancellationToken);
        if (project is null)
        {
            project = new Project
            {
                ProgramName = name,
                CustomerName = customer,
                SalesOrderNumber = salesOrder,
                JobNumber = jobNumber,
                ProgramManager = manager,
                Engineer = engineer,
                ProgramStart = programStart,
                TargetDelivery = tasks.Max(task => task.EndDate),
                PriorityRank = (await db.Projects
                    .Where(candidate => candidate.Status != ProjectStatus.Complete)
                    .Select(candidate => candidate.PriorityRank)
                    .MaxAsync(cancellationToken) ?? 0) + 1,
                Status = ProjectStatus.NotStarted,
                CurrentTask = tasks.OrderBy(task => task.Sequence).First().Title,
                Tasks = tasks.ToList()
            };
            db.Projects.Add(project);
            await db.SaveChangesAsync(cancellationToken);
        }
        else
        {
            project.CustomerName ??= customer;
            project.SalesOrderNumber ??= salesOrder;
            project.JobNumber ??= jobNumber;
            project.ProgramManager ??= manager;
            project.Engineer ??= engineer;
            if (project.Tasks.Count == 0)
            {
                project.Tasks.AddRange(tasks);
            }
        }

        foreach (var item in messages.Where(item => project.Messages.All(message => message.Body != item.Body)))
        {
            var message = new ProjectMessage
            {
                Project = project,
                ProjectId = project.Id,
                AuthorDisplayName = item.Author,
                AuthorAccountName = item.Account,
                Body = item.Body,
                CreatedAt = item.At
            };
            project.Messages.Add(message);
            if (item.Body.Contains("@ProjectTrackerAdmin", StringComparison.OrdinalIgnoreCase))
            {
                newMentionMessages.Add(message);
            }
        }
    }

    private static async Task EnsureCompletedProjectAsync(
        ProjectTrackerDbContext db,
        string name,
        string customer,
        string salesOrder,
        string jobNumber,
        DateOnly start,
        DateOnly plannedFinish,
        DateOnly actualFinish,
        string completionNote,
        CancellationToken cancellationToken)
    {
        var project = await db.Projects
            .Include(candidate => candidate.Tasks)
            .Include(candidate => candidate.Messages)
            .FirstOrDefaultAsync(candidate => candidate.ProgramName == name, cancellationToken);
        if (project is null)
        {
            project = new Project { ProgramName = name };
            db.Projects.Add(project);
        }

        project.CustomerName ??= customer;
        project.SalesOrderNumber ??= salesOrder;
        project.JobNumber ??= jobNumber;
        project.ProgramManager ??= "Taylor Reed";
        project.Engineer ??= "Dana Cruz";
        project.ProgramStart ??= start;
        project.TargetDelivery ??= plannedFinish;
        var scheduleVarianceDays = actualFinish.DayNumber - plannedFinish.DayNumber;
        var effectivePlannedFinish = project.TargetDelivery.Value;
        var effectiveActualFinish = effectivePlannedFinish.AddDays(scheduleVarianceDays);
        project.CompletedOn = effectiveActualFinish;
        project.PriorityRank = null;
        project.Progress = 1m;
        project.Status = ProjectStatus.Complete;
        project.CurrentTask = "Program Complete";

        if (project.Tasks.Count == 0)
        {
            project.Tasks.AddRange(BuildCompletedTasks(
                project.ProgramStart.Value,
                effectivePlannedFinish,
                effectiveActualFinish,
                completionNote));
        }
        else
        {
            var finalTask = project.Tasks.OrderBy(task => task.Sequence).Last();
            finalTask.OriginalEndDate = effectivePlannedFinish;
            finalTask.EndDate = effectiveActualFinish;
            finalTask.Notes = completionNote;
            finalTask.NoteUpdatedAt = effectiveActualFinish.ToDateTime(new TimeOnly(16, 0), DateTimeKind.Local);
        }

        var chat = new[]
        {
            ("Taylor Reed", @"SON-AERO\taylor.reed", $"Final review complete for {name}. {completionNote}", effectiveActualFinish.ToDateTime(new TimeOnly(10, 0), DateTimeKind.Local)),
            ("Dana Cruz", @"SON-AERO\dana.cruz", "All operation records and inspection results are closed.", effectiveActualFinish.ToDateTime(new TimeOnly(13, 30), DateTimeKind.Local)),
            ("Taylor Reed", @"SON-AERO\taylor.reed", "Lessons learned were captured for the next production release.", effectiveActualFinish.AddDays(1).ToDateTime(new TimeOnly(9, 15), DateTimeKind.Local))
        };
        foreach (var item in chat)
        {
            var existingMessage = project.Messages.FirstOrDefault(message => message.Body == item.Item3);
            if (existingMessage is not null)
            {
                existingMessage.CreatedAt = new DateTimeOffset(item.Item4);
                continue;
            }

            project.Messages.Add(new ProjectMessage
            {
                Project = project,
                AuthorDisplayName = item.Item1,
                AuthorAccountName = item.Item2,
                Body = item.Item3,
                CreatedAt = new DateTimeOffset(item.Item4)
            });
        }
    }

    private static List<ProjectTask> BuildTest5Tasks() =>
    [
        Task(1, "Material & Planning", "Planning", new(2026, 7, 20), new(2026, 7, 30), 0.35m, "Material released; @casey.lee confirm the mill reservation."),
        Task(2, "Primary CNC Machining", "CNC Mill", new(2026, 8, 3), new(2026, 8, 20), 0m),
        Task(3, "In-Process Inspection", "Quality Lab", new(2026, 8, 24), new(2026, 8, 27), 0m),
        Task(4, "Secondary Machining", "CNC Mill 2", new(2026, 8, 31), new(2026, 9, 17), 0m),
        Task(5, "Outside Processing", "Outside Processing", new(2026, 9, 21), new(2026, 10, 1), 0m),
        Task(6, "Final Inspection", "Quality Lab", new(2026, 10, 5), new(2026, 10, 12), 0m),
        Task(7, "Documentation & Ship", "Shipping", new(2026, 10, 13), new(2026, 10, 16), 0m)
    ];

    private static List<ProjectTask> BuildTest6Tasks() =>
    [
        Task(1, "Engineering Release", "Engineering", new(2026, 7, 27), new(2026, 8, 6), 0m, "@alex.morgan programming package is ready for peer review."),
        Task(2, "Rough CNC Machining", "CNC Mill", new(2026, 8, 10), new(2026, 8, 27), 0m),
        Task(3, "First Article Inspection", "Quality Lab", new(2026, 8, 31), new(2026, 9, 3), 0m),
        Task(4, "Finish Machining", "CNC Mill 2", new(2026, 9, 8), new(2026, 9, 24), 0m),
        Task(5, "Heat Treat", "Outside Processing", new(2026, 9, 28), new(2026, 10, 8), 0m),
        Task(6, "Final Inspection", "Quality Lab", new(2026, 10, 12), new(2026, 10, 22), 0m),
        Task(7, "Pack & Delivery", "Shipping", new(2026, 10, 26), new(2026, 10, 30), 0m)
    ];

    private static IEnumerable<ProjectTask> BuildCompletedTasks(
        DateOnly start,
        DateOnly plannedFinish,
        DateOnly actualFinish,
        string completionNote)
    {
        var midpoint = start.AddDays(Math.Max(14, (plannedFinish.DayNumber - start.DayNumber) / 2));
        return
        [
            CompletedTask(1, "Planning & Material", "Planning", start, start.AddDays(10), start, start.AddDays(10), "Planning package approved."),
            CompletedTask(2, "Production", "CNC Mill", start.AddDays(11), midpoint, start.AddDays(11), midpoint, "Machining and in-process inspection complete."),
            CompletedTask(3, "Final Inspection & Ship", "Quality Lab", midpoint.AddDays(1), plannedFinish, midpoint.AddDays(1), actualFinish, completionNote)
        ];
    }

    private static ProjectTask Task(
        int sequence,
        string title,
        string workStation,
        DateOnly start,
        DateOnly end,
        decimal percent,
        string? note = null) =>
        new()
        {
            Sequence = sequence,
            ExternalTaskId = sequence.ToString(),
            Title = title,
            WorkStation = workStation,
            StartDate = start,
            StartDateLocked = true,
            OriginalStartDate = start,
            EndDate = end,
            OriginalEndDate = end,
            EstimatedDuration = Math.Max(1, end.DayNumber - start.DayNumber + 1),
            PercentComplete = percent,
            PercentCompleteManual = true,
            Notes = note,
            NoteUpdatedAt = note is null ? null : new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero)
        };

    private static ProjectTask CompletedTask(
        int sequence,
        string title,
        string workStation,
        DateOnly plannedStart,
        DateOnly plannedEnd,
        DateOnly actualStart,
        DateOnly actualEnd,
        string note) =>
        new()
        {
            Sequence = sequence,
            ExternalTaskId = sequence.ToString(),
            Title = title,
            WorkStation = workStation,
            OriginalStartDate = plannedStart,
            OriginalEndDate = plannedEnd,
            StartDate = actualStart,
            StartDateLocked = true,
            EndDate = actualEnd,
            EstimatedDuration = Math.Max(1, actualEnd.DayNumber - actualStart.DayNumber + 1),
            PercentComplete = 1m,
            PercentCompleteManual = true,
            Status = TaskScheduleStatus.Complete,
            Notes = note,
            NoteUpdatedAt = new DateTimeOffset(actualEnd.ToDateTime(new TimeOnly(16, 0)))
        };
}
