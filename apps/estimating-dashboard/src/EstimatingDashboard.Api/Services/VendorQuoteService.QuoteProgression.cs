using EstimatingDashboard.Api.Auth;
using EstimatingDashboard.Api.Models;

namespace EstimatingDashboard.Api.Services;

public sealed partial class VendorQuoteService
{
    internal Task TryAdvanceQuoteRfqAsync(EstimatingQuoteHistoryRecord quote, EstimatingAccessProfile access,
        CancellationToken ct) => progression?.TryAdvanceAsync(quote, access, ct) ?? Task.CompletedTask;
}
