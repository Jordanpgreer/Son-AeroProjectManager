using System.Text.Json;
using EstimatingDashboard.Api.Auth;
using EstimatingDashboard.Api.Data;
using EstimatingDashboard.Api.Dtos;
using EstimatingDashboard.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace EstimatingDashboard.Api.Services;

internal sealed class QuoteOutsideProcessingService(EstimatingAccessDbContext db, VendorQuoteService vendors,
    IFulcrumOutsideProcessingReader requirements, TimeProvider clock)
{
    public async Task<OutsideProcessingRequirementsDto> ReadAsync(int id, EstimatingAccessProfile access, CancellationToken ct)
    {
        var quote = await vendors.QuoteAsync(id, access, false, ct);
        var state = await StateAsync(id, ct);
        return await WithRequestsAsync(await InspectAsync(quote, state, ct), state, access, ct);
    }

    public async Task<MaterialRfqDraftFile> DraftAsync(int id, string operationKey, EstimatingAccessProfile access, CancellationToken ct)
    {
        var quote = await vendors.QuoteAsync(id, access, true, ct);
        if (operationKey.Length != 64 || !operationKey.All(char.IsAsciiHexDigit))
            throw new VendorQuoteException(400, "Choose a valid outside processing operation.");
        var state = await StateAsync(id, ct);
        var inspection = await InspectAsync(quote, state, ct);
        if (inspection.Warnings.Count > 0)
            throw new VendorQuoteException(409, "Confirm current quote quantities before opening an OP draft.");
        var operation = inspection.Operations.SingleOrDefault(x => x.OperationKey == operationKey)
            ?? throw new VendorQuoteException(404, "The OP operation is no longer on the current Fulcrum quote.");
        if (operation.Status != "needs-rfq")
            throw new VendorQuoteException(409, "Review this OP's routing, vendor, and quantities in Fulcrum before opening a draft.");
        var fingerprint = Fingerprint(operation);
        var links = MaterialDraftTracking.Read(state).ToList();
        var previous = links.SingleOrDefault(x => x.Kind == "op" && x.ItemId == operationKey);
        var sameGeneration = previous is { IsCurrent: true } && previous.Fingerprint == fingerprint
            && previous.QuantitiesFingerprint == MaterialDraftTracking.QuantitiesFingerprint(state)
            && MaterialDraftTracking.ValidToken(previous.Token);
        var token = sameGeneration ? previous!.Token! : Guid.NewGuid().ToString("N");
        var draft = OutsideProcessingRfqDraft.Create(inspection.QuoteNumber, operation, token);
        var requests = await db.Set<VendorQuoteRequest>().Where(x => x.QuoteHistoryId == id).ToListAsync(ct);
        var existing = requests.FirstOrDefault(x => x.Id == previous?.RequestId
            && string.Equals(x.PartNumber, operation.PartNumber, StringComparison.OrdinalIgnoreCase)
            && operation.VendorEmails.Contains(x.VendorEmail, StringComparer.OrdinalIgnoreCase));
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        int requestId;
        if (existing is null)
            requestId = (await vendors.CreateAsync(new(id, operation.VendorName ?? "", operation.VendorEmails[0],
                RequestTitle(operation), PartNumber: operation.PartNumber), access, ct)).Request.Id;
        else
        {
            requestId = existing.Id;
            if (!sameGeneration)
                await vendors.UpdateAsync(existing.Id, new(existing.Version, existing.VendorName, existing.Title,
                    VendorQuoteStatuses.Untouched, existing.FollowUpDate,
                    "A new OP draft was prepared for current Fulcrum routing. Confirm sending before marking Waiting on vendor."), access, ct);
        }
        links.RemoveAll(x => x.Kind == "op" && x.ItemId == operationKey);
        links.Add(sameGeneration && previous!.RequestId == requestId ? previous : new(operationKey, requestId, fingerprint,
            token, clock.GetUtcNow(), operation.PartNumber, $"Quote {inspection.QuoteNumber} - {operation.PartNumber}",
            draft.PlainText, operation.VendorEmails.Select(email => email.ToLowerInvariant()).Distinct().ToArray(),
            MaterialDraftTracking.QuantitiesFingerprint(state), Kind: "op"));
        state ??= NewState(quote);
        state.DraftsJson = JsonSerializer.Serialize(links);
        Touch(state, access);
        await vendors.SaveAsync(ct);
        await transaction.CommitAsync(ct);
        return draft;
    }

    private async Task<OutsideProcessingRequirementsDto> InspectAsync(EstimatingQuoteHistoryRecord quote,
        MaterialPurchasingState? state, CancellationToken ct)
    {
        var selections = JsonSerializer.Deserialize<MaterialQuantitySelectionDto[]>(state?.QuantitiesJson ?? "[]") ?? [];
        var quantities = selections.ToDictionary(x => x.ItemId,
            x => (IReadOnlyList<decimal>)new[] { x.Minimum, x.Maximum ?? x.Minimum }.Distinct().ToArray());
        return await requirements.InspectAsync(quote.Id, quote.QuoteNumber, quote.SourceId, quantities, ct);
    }

    private async Task<OutsideProcessingRequirementsDto> WithRequestsAsync(OutsideProcessingRequirementsDto result,
        MaterialPurchasingState? state, EstimatingAccessProfile access, CancellationToken ct)
    {
        var links = MaterialDraftTracking.Read(state);
        if (state is not null && links.Any(link => link.Kind == "op" && link.IsCurrent && !result.Operations.Any(op =>
                op.OperationKey == link.ItemId && Fingerprint(op) == link.Fingerprint)))
        {
            links = links.Select(link => link.Kind == "op" && link.IsCurrent && !result.Operations.Any(op =>
                    op.OperationKey == link.ItemId && Fingerprint(op) == link.Fingerprint)
                ? link with { IsCurrent = false } : link).ToArray();
            if (!access.IsPreview && VendorQuoteService.Has(access, EstimatingPermissions.ManageQuotes))
            {
                state.DraftsJson = JsonSerializer.Serialize(links);
                Touch(state, access);
                await vendors.SaveAsync(ct);
            }
        }
        var requests = await db.Set<VendorQuoteRequest>().AsNoTracking().Where(x => x.QuoteHistoryId == result.QuoteHistoryId).ToListAsync(ct);
        return result with { Operations = result.Operations.Select(op =>
        {
            var link = links.SingleOrDefault(x => x.Kind == "op" && x.IsCurrent && x.ItemId == op.OperationKey
                && x.Fingerprint == Fingerprint(op)
                && x.QuantitiesFingerprint == MaterialDraftTracking.QuantitiesFingerprint(state));
            var request = requests.SingleOrDefault(x => x.Id == link?.RequestId
                && string.Equals(x.PartNumber, op.PartNumber, StringComparison.OrdinalIgnoreCase)
                && op.VendorEmails.Contains(x.VendorEmail, StringComparer.OrdinalIgnoreCase));
            return op with { RequestId = request?.Id, RequestStatus = request is null ? null : VendorQuoteStatuses.Normalize(request.Status),
                SentAt = request is null ? null : link?.SentAt };
        }).ToArray() };
    }

    internal static string Fingerprint(OutsideProcessingOperationDto op) => VendorQuoteService.Hash(JsonSerializer.Serialize(new
    {
        op.OperationKey, op.ItemId, op.OperationId, op.PartNumber, op.Name, op.Instructions,
        op.RequiredMinimum, op.RequiredMaximum, op.QuoteQuantities, op.VendorId,
        Emails = op.VendorEmails.Order(StringComparer.OrdinalIgnoreCase).Select(x => x.ToLowerInvariant()).ToArray()
    }));
    private static string RequestTitle(OutsideProcessingOperationDto op)
    {
        var suffix = $" [{op.OperationId}]";
        const string prefix = "Outside processing: ";
        var limit = Math.Max(0, 240 - prefix.Length - suffix.Length);
        return prefix + op.Name[..Math.Min(limit, op.Name.Length)] + suffix;
    }
    private Task<MaterialPurchasingState?> StateAsync(int id, CancellationToken ct) =>
        db.Set<MaterialPurchasingState>().SingleOrDefaultAsync(x => x.QuoteHistoryId == id, ct);
    private MaterialPurchasingState NewState(EstimatingQuoteHistoryRecord quote)
    { var state = new MaterialPurchasingState { QuoteHistory = quote, QuoteHistoryId = quote.Id }; db.Add(state); return state; }
    private void Touch(MaterialPurchasingState state, EstimatingAccessProfile access)
    { state.Version++; state.UpdatedAt = clock.GetUtcNow(); state.UpdatedBy = access.AccountName; }
}
