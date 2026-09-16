using EstimatingDashboard.Api.Auth;
using EstimatingDashboard.Api.Dtos;
using EstimatingDashboard.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace EstimatingDashboard.Api.Services;

public sealed partial class QuoteStatusService
{
    public async Task<QuoteStatusDetailDto> SetEmailRateRequestAsync(int id, long messageId, QuoteEmailRateRequestDto dto,
        EstimatingAccessProfile access, CancellationToken ct)
    {
        var quote = await LifecycleQuoteAsync(id, dto.ExpectedVersion, access, false, ct);
        var message = await LifecycleEmailAsync(id, messageId, ct);
        if (message.RemovedAt.HasValue) throw new VendorQuoteException(409, "Restore this email before changing its label.");
        if (message.Direction != "outgoing" || !message.RequestId.HasValue)
            throw new VendorQuoteException(400, "Rates requested requires a sent email assigned to an RFQ.");
        var request = await db.Set<VendorQuoteRequest>().SingleOrDefaultAsync(
            x => x.Id == message.RequestId && x.QuoteHistoryId == id, ct)
            ?? throw new VendorQuoteException(400, "Choose an RFQ belonging to this quote.");
        if (message.IsRateRequest == dto.IsRateRequest) return await DetailAsync(id, access, ct);
        message.IsRateRequest = dto.IsRateRequest;
        var now = clock.GetUtcNow();
        request.Activity.Add(new VendorQuoteActivity {
            Request = request, Kind = "email-rate-request",
            Text = $"{(dto.IsRateRequest ? "Email marked as Rates requested" : "Rates requested label removed")} (#{message.Id})",
            OldValue = dto.IsRateRequest ? null : message.Subject, NewValue = dto.IsRateRequest ? message.Subject : null,
            OccurredAt = now, AccountName = access.AccountName, DisplayName = access.DisplayName });
        if (dto.IsRateRequest && VendorQuoteStatuses.Normalize(request.Status) != VendorQuoteStatuses.WaitingOnVendor)
        {
            request.Activity.Add(new VendorQuoteActivity {
                Request = request, Kind = "status", Text = "Status changed", OldValue = request.Status,
                NewValue = VendorQuoteStatuses.WaitingOnVendor, OccurredAt = now,
                AccountName = access.AccountName, DisplayName = access.DisplayName });
            request.Status = VendorQuoteStatuses.WaitingOnVendor;
            request.StatusChangedAt = now; request.StatusChangedBy = access.DisplayName;
        }
        request.UpdatedAt = now; request.Version++;
        Touch(quote, access);
        // Persist the label, RFQ status, concurrency versions, and audit trail together.
        await vendors.SaveAsync(ct);
        return await DetailAsync(id, access, ct);
    }
}
