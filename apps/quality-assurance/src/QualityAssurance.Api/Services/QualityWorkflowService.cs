using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QualityAssurance.Api.Auth;
using QualityAssurance.Api.Data;
using QualityAssurance.Api.Dtos;
using QualityAssurance.Api.Models;
using SonAero.Platform.Security;

namespace QualityAssurance.Api.Services;

public sealed partial class QualityWorkflowService(
    QualityAssuranceDbContext db, IQualityAssuranceAccessStore accessStore, IConfiguration? configuration = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<QualityWorkflowDto> GetAsync(QualityAssuranceAccessProfile actor, CancellationToken cancellationToken)
    {
        EnsureManager(actor);
        var workflow = await db.Workflows.AsNoTracking().SingleOrDefaultAsync(
            item => item.Module == QualityWorkflowGraphEngine.Module, cancellationToken);
        var draft = workflow is null ? await CreateInitialGraphAsync(cancellationToken) : ReadGraph(workflow.DraftJson);
        var history = await db.WorkflowAuditEntries.AsNoTracking()
            .Where(entry => entry.Module == QualityWorkflowGraphEngine.Module)
            .OrderByDescending(entry => entry.Id).Take(20)
            .Select(entry => new QualityWorkflowHistoryDto(entry.Id, entry.Action, entry.Revision,
                entry.AccountName, entry.DisplayName, entry.OccurredAt)).ToListAsync(cancellationToken);
        return new(QualityWorkflowGraphEngine.Module, workflow?.Version ?? 0, workflow?.PublishedRevision ?? 0,
            workflow?.PublishedAt, workflow?.PublishedBy, workflow?.UpdatedAt, workflow?.UpdatedBy,
            draft, workflow?.PublishedJson is { } published ? ReadGraph(published) : null,
            await ValidateAsync(draft, cancellationToken), history);
    }

    public async Task<QualityWorkflowDto> SaveAsync(
        QualityWorkflowSaveDto dto, QualityAssuranceAccessProfile actor, CancellationToken cancellationToken)
    {
        EnsureManager(actor);
        QualityWorkflowGraphEngine.EnsureBounded(dto.Graph);
        var workflow = await db.Workflows.SingleOrDefaultAsync(
            item => item.Module == QualityWorkflowGraphEngine.Module, cancellationToken);
        var creating = workflow is null;
        if (dto.Version != (workflow?.Version ?? 0)) throw Conflict();
        if (workflow is null)
        {
            workflow = new QualityWorkflow();
            db.Workflows.Add(workflow);
        }
        else db.Entry(workflow).Property(item => item.Version).OriginalValue = dto.Version;
        workflow.DraftJson = JsonSerializer.Serialize(dto.Graph with { Name = dto.Graph.Name.Trim() }, JsonOptions);
        workflow.Version++;
        workflow.UpdatedAt = DateTimeOffset.UtcNow;
        workflow.UpdatedBy = actor.AccountName;
        AddDefinitionAudit(workflow, "DraftSaved", actor);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException exception) when (creating)
        {
            // A unique module constraint also protects simultaneous first saves.
            throw new DbUpdateConcurrencyException("The workflow changed. Reload before saving.", exception);
        }
        return await GetAsync(actor, cancellationToken);
    }

    public async Task<QualityWorkflowDto> PublishAsync(
        long version, QualityAssuranceAccessProfile actor, CancellationToken cancellationToken)
    {
        EnsureManager(actor);
        var workflow = await db.Workflows.SingleOrDefaultAsync(
            item => item.Module == QualityWorkflowGraphEngine.Module, cancellationToken)
            ?? throw new ArgumentException("Save the workflow draft before publishing.");
        if (version != workflow.Version) throw Conflict();
        var validation = await ValidateAsync(ReadGraph(workflow.DraftJson), cancellationToken);
        if (!validation.IsValid) throw new ArgumentException(string.Join(" ", validation.Issues.Select(issue => issue.Message).Distinct()));
        db.Entry(workflow).Property(item => item.Version).OriginalValue = version;
        workflow.PublishedJson = workflow.DraftJson;
        workflow.PublishedRevision++;
        workflow.PublishedAt = DateTimeOffset.UtcNow;
        workflow.PublishedBy = actor.AccountName;
        workflow.UpdatedAt = workflow.PublishedAt.Value;
        workflow.UpdatedBy = actor.AccountName;
        workflow.Version++;
        AddDefinitionAudit(workflow, "Published", actor);
        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(actor, cancellationToken);
    }

    public async Task<QualityWorkflowSimulation> SimulateAsync(
        QualityWorkflowSimulationDto dto, QualityAssuranceAccessProfile actor, CancellationToken cancellationToken)
    {
        EnsureManager(actor);
        if (!QualityWorkflowGraphEngine.Triggers.Contains(dto.Trigger)) throw new ArgumentException("Select a supported Quality action.");
        if (dto.Context is null || dto.Context.ActorGroupIds?.Count > 100
            || new[] { dto.Context.Customer, dto.Context.TaskType, dto.Context.Status, dto.Context.HoldReason }.Any(value => value?.Length > 1000))
            throw new ArgumentException("Test values are missing or too long.");
        var validation = await ValidateAsync(dto.Graph, cancellationToken);
        if (!validation.IsValid) return new(false, validation.Issues, [], "blocked", null, null, null, "Fix the workflow issues before testing.");
        var context = dto.Trigger switch
        {
            "qa-completed" => dto.Context with { Status = "Ready to Ship" },
            "shipment-shipped" => dto.Context with { Status = "Shipped" },
            _ => dto.Context
        };
        return QualityWorkflowGraphEngine.Evaluate(dto.Graph, dto.Trigger, context);
    }

    public async Task<QualityWorkflowValidation> ValidateAsync(QualityWorkflowGraph graph, CancellationToken cancellationToken)
    {
        var result = QualityWorkflowGraphEngine.Validate(graph);
        var issues = result.Issues.ToList();
        var groups = await accessStore.GetGroupsWithPermissionAsync(QualityAssurancePermissions.ResponsibleGroupEligible, cancellationToken);
        var users = await accessStore.GetUsersWithPermissionAsync(QualityAssurancePermissions.AssignmentEligible, cancellationToken);
        var allGroups = await accessStore.GetGroupsAsync(cancellationToken);
        foreach (var node in graph.Nodes)
        {
            if (node.Type == "trigger" && node.AllowedGroupIds?.Any(id => allGroups.All(group => group.Id != id)) == true)
                issues.Add(new(node.Id, "A trigger access group no longer exists."));
            if (node.Type != "route") continue;
            if (groups.All(group => group.Id != node.TargetGroupId))
                issues.Add(new(node.Id, "Select an active Quality Responsible Group for this queue."));
            if (node.AssignmentMode == "SpecificUser" && !users.Any(user => user.Id == node.TargetUserId
                && node.TargetGroupId.HasValue && user.GroupIds.Contains(node.TargetGroupId.Value)))
                issues.Add(new(node.Id, "The selected person must be eligible for Quality assignment and belong to the destination group."));
        }
        return new(issues.Count == 0, issues);
    }

    private void AddDefinitionAudit(QualityWorkflow workflow, string action, QualityAssuranceAccessProfile actor) =>
        db.WorkflowAuditEntries.Add(new QualityWorkflowAuditEntry
        {
            Action = action, Revision = workflow.PublishedRevision, GraphJson = workflow.DraftJson,
            AccountName = actor.AccountName, DisplayName = actor.DisplayName, OccurredAt = DateTimeOffset.UtcNow
        });

    private static void EnsureManager(QualityAssuranceAccessProfile actor)
    {
        if (!actor.HasPermission(QualityAssurancePermissions.RulesManage))
            throw new UnauthorizedAccessException("You do not have permission to manage Quality workflows.");
    }

    private static DbUpdateConcurrencyException Conflict() => new("The workflow changed. Reload before saving or publishing.");
    private static QualityWorkflowGraph ReadGraph(string json) => JsonSerializer.Deserialize<QualityWorkflowGraph>(json, JsonOptions)
        ?? throw new InvalidOperationException("The saved workflow is empty.");
}
