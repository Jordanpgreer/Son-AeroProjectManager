using System.Text.Json;
using EstimatingDashboard.Api.Auth;
using EstimatingDashboard.Api.Data;
using EstimatingDashboard.Api.Dtos;
using EstimatingDashboard.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace EstimatingDashboard.Api.Services;

internal sealed class QuoteMaterialPurchasingService(EstimatingAccessDbContext db, VendorQuoteService vendors,
    IFulcrumMaterialRequirementsReader requirements, TimeProvider clock)
{
    public async Task<MaterialRequirementsDto> ReadAsync(int id, EstimatingAccessProfile access, CancellationToken ct)
    {
        // Quote authorization precedes state access and all provider requests.
        var quote = await vendors.QuoteAsync(id, access, false, ct);
        var state = await StateAsync(id, ct);
        return await WithRequestsAsync(await InspectAsync(quote, Selections(state), ct), state, access, ct);
    }

    public async Task<MaterialRequirementsDto> SaveQuantitiesAsync(int id, SaveMaterialQuantitiesDto dto,
        EstimatingAccessProfile access, CancellationToken ct)
    {
        var quote = await vendors.QuoteAsync(id, access, true, ct);
        var selections = Validate(dto.Quantities);
        var state = await StateAsync(id, ct);
        VendorQuoteService.Version(state?.Version ?? 0, dto.ExpectedVersion);
        // Check current Fulcrum parts and compute successfully before saving corrections.
        var result = await InspectAsync(quote, selections, ct);
        if (selections.Any(selection => !result.Parts.Any(part => part.ItemId == selection.ItemId)))
            throw new VendorQuoteException(400, "A selected part is no longer on this Fulcrum quote. Refresh and confirm its current parts.");
        state ??= NewState(quote);
        state.QuantitiesJson = JsonSerializer.Serialize(selections);
        Touch(state, access);
        var audit = string.Join("; ", result.Parts.Where(p => p.QuantitiesOverridden)
            .Select(p => $"{p.PartNumber}: {string.Join(" / ", p.Quantities)}"));
        db.Add(new QuoteStatusActivity { QuoteHistoryId = id, Kind = "details",
            Text = selections.Count == 0 ? "Material quantities reset to Fulcrum" : "Material quote quantities confirmed in Arda",
            NewValue = audit[..Math.Min(4000, audit.Length)], OccurredAt = clock.GetUtcNow(),
            AccountName = access.AccountName, DisplayName = access.DisplayName });
        await vendors.SaveAsync(ct);
        return await WithRequestsAsync(result, state, access, ct);
    }

    public async Task<MaterialRfqDraftFile> DraftAsync(int id, string itemId, EstimatingAccessProfile access, CancellationToken ct)
    {
        var quote = await vendors.QuoteAsync(id, access, true, ct);
        if (string.IsNullOrWhiteSpace(itemId) || itemId.Length > 100 || itemId.Any(char.IsControl))
            throw new VendorQuoteException(400, "Choose a valid material from this quote.");
        var state = await StateAsync(id, ct);
        // Fresh data prevents obsolete browser quantities, stock decisions, and recipients.
        var review = await InspectAsync(quote, Selections(state), ct);
        var material = review.Materials.SingleOrDefault(x => x.ItemId == itemId)
            ?? throw new VendorQuoteException(404, "This material is not on the quote's current production BOM.");
        var fingerprint = Fingerprint(material);
        var links = Drafts(state).ToList();
        var previous = links.SingleOrDefault(x => x.ItemId == itemId);
        var sameGeneration = previous is { IsCurrent: true } && previous.Fingerprint == fingerprint
            && previous.QuantitiesFingerprint == MaterialDraftTracking.QuantitiesFingerprint(state)
            && MaterialDraftTracking.ValidToken(previous.Token);
        var token = sameGeneration ? previous!.Token! : Guid.NewGuid().ToString("N");
        var draft = MaterialRfqDraft.Create(review.QuoteNumber, material, token);
        var requests = await db.Set<VendorQuoteRequest>().Where(x => x.QuoteHistoryId == id).ToListAsync(ct);
        var matching = requests.Where(x => string.Equals(x.PartNumber, material.PartNumber, StringComparison.OrdinalIgnoreCase)
            && material.VendorEmails.Contains(x.VendorEmail, StringComparer.OrdinalIgnoreCase)).ToList();
        var existing = matching.FirstOrDefault(x => x.Id == previous?.RequestId)
            ?? matching.FirstOrDefault(x => string.Equals(x.VendorEmail, material.VendorEmails[0], StringComparison.OrdinalIgnoreCase))
            ?? matching.OrderBy(x => x.Id).FirstOrDefault();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        int requestId;
        if (existing is null)
            requestId = (await vendors.CreateAsync(new(id, material.VendorName ?? "", material.VendorEmails[0],
                $"Material pricing: {material.PartNumber}", PartNumber: material.PartNumber), access, ct)).Request.Id;
        else
        {
            requestId = existing.Id;
            if (!sameGeneration)
                await vendors.UpdateAsync(existing.Id, new(existing.Version, existing.VendorName, existing.Title,
                    VendorQuoteStatuses.Untouched, existing.FollowUpDate,
                    "A new material draft was prepared for the current full requirements. Confirm sending before marking Waiting on vendor."), access, ct);
        }
        links.RemoveAll(x => x.ItemId == itemId);
        links.Add(sameGeneration && previous!.RequestId == requestId ? previous : new(itemId, requestId, fingerprint,
            token, clock.GetUtcNow(), material.PartNumber, $"Quote {review.QuoteNumber} - {material.PartNumber}",
            draft.PlainText, material.VendorEmails.Select(email => email.ToLowerInvariant()).Distinct().ToArray(),
            MaterialDraftTracking.QuantitiesFingerprint(state)));
        state ??= NewState(quote);
        state.DraftsJson = JsonSerializer.Serialize(links);
        Touch(state, access);
        await vendors.SaveAsync(ct);
        await transaction.CommitAsync(ct);
        // Preparing a draft never labels a message sent or advances its RFQ to Waiting on vendor.
        return draft;
    }

    private Task<MaterialRequirementsDto> InspectAsync(EstimatingQuoteHistoryRecord quote,
        IReadOnlyList<MaterialQuantitySelectionDto> selections, CancellationToken ct) =>
        requirements.InspectAsync(quote.Id, quote.QuoteNumber, quote.SourceId, ct,
            selections.ToDictionary(x => x.ItemId, x => (IReadOnlyList<decimal>)new[] { x.Minimum, x.Maximum ?? x.Minimum }.Distinct().ToArray()));

    private async Task<MaterialRequirementsDto> WithRequestsAsync(MaterialRequirementsDto result, MaterialPurchasingState? state,
        EstimatingAccessProfile access, CancellationToken ct)
    {
        var links = Drafts(state);
        if (state is not null && links.Any(link => link.IsCurrent && !result.Materials.Any(material =>
                material.ItemId == link.ItemId && Fingerprint(material) == link.Fingerprint)))
        {
            links = links.Select(link => link.IsCurrent && !result.Materials.Any(material =>
                    material.ItemId == link.ItemId && Fingerprint(material) == link.Fingerprint)
                ? link with { IsCurrent = false } : link).ToArray();
            // Preview/read-only inspection may hide obsolete tracking locally but cannot mutate it.
            if (!access.IsPreview && VendorQuoteService.Has(access, EstimatingPermissions.ManageQuotes))
            {
                state.DraftsJson = JsonSerializer.Serialize(links);
                Touch(state, access);
                await vendors.SaveAsync(ct);
            }
        }
        var requests = await db.Set<VendorQuoteRequest>().AsNoTracking().Where(x => x.QuoteHistoryId == result.QuoteHistoryId).ToListAsync(ct);
        return result with { QuantityVersion = state?.Version ?? 0, Materials = result.Materials.Select(material =>
        {
            var link = links.SingleOrDefault(x => x.IsCurrent && x.ItemId == material.ItemId && x.Fingerprint == Fingerprint(material)
                && (x.Token is null || x.QuantitiesFingerprint == MaterialDraftTracking.QuantitiesFingerprint(state)));
            var request = requests.SingleOrDefault(x => x.Id == link?.RequestId
                && string.Equals(x.PartNumber, material.PartNumber, StringComparison.OrdinalIgnoreCase)
                && material.VendorEmails.Contains(x.VendorEmail, StringComparer.OrdinalIgnoreCase));
            return material with { RequestId = request?.Id, RequestStatus = request is null ? null : VendorQuoteStatuses.Normalize(request.Status) };
        }).ToList() };
    }

    internal static string Fingerprint(MaterialRequirementDto material) => VendorQuoteService.Hash(JsonSerializer.Serialize(new
    {
        material.ItemId, material.PartNumber, material.Description, material.UnitOfMeasure,
        material.RequiredMinimum, material.RequiredMaximum, material.QuoteQuantities, material.VendorId,
        Emails = material.VendorEmails.Order(StringComparer.OrdinalIgnoreCase).Select(x => x.ToLowerInvariant()).ToArray()
    }));
    private Task<MaterialPurchasingState?> StateAsync(int id, CancellationToken ct) =>
        db.Set<MaterialPurchasingState>().SingleOrDefaultAsync(x => x.QuoteHistoryId == id, ct);
    private MaterialPurchasingState NewState(EstimatingQuoteHistoryRecord quote)
    { var state = new MaterialPurchasingState { QuoteHistory = quote, QuoteHistoryId = quote.Id }; db.Add(state); return state; }
    private void Touch(MaterialPurchasingState state, EstimatingAccessProfile access)
    { state.Version++; state.UpdatedAt = clock.GetUtcNow(); state.UpdatedBy = access.AccountName; }
    private static IReadOnlyList<MaterialQuantitySelectionDto> Selections(MaterialPurchasingState? state) =>
        JsonSerializer.Deserialize<MaterialQuantitySelectionDto[]>(state?.QuantitiesJson ?? "[]") ?? [];
    private static IReadOnlyList<MaterialDraftLink> Drafts(MaterialPurchasingState? state) => MaterialDraftTracking.Read(state);

    private static IReadOnlyList<MaterialQuantitySelectionDto> Validate(IReadOnlyList<MaterialQuantitySelectionDto>? selections)
    {
        if (selections is null || selections.Count > 200
            || selections.Any(x => x is null || string.IsNullOrWhiteSpace(x.ItemId) || x.ItemId.Length > 100
                || x.ItemId.Any(char.IsControl) || x.Minimum <= 0 || x.Minimum > 1000000000m
                || x.Maximum.HasValue && (x.Maximum < x.Minimum || x.Maximum > 1000000000m))
            || selections.Select(x => x.ItemId).Distinct(StringComparer.Ordinal).Count() != selections.Count)
            throw new VendorQuoteException(400, "Enter one positive quantity, or a lowest and highest quantity, for each part. The maximum is 1,000,000,000.");
        return selections;
    }
}
