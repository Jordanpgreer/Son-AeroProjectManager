using System.Net.Mail;
using EstimatingDashboard.Api.Auth;
using EstimatingDashboard.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace EstimatingDashboard.Api.Endpoints;

internal static class MaterialFollowUpEndpoints
{
    internal static void MapMaterialFollowUpActions(this RouteGroupBuilder materials)
    {
        var group = materials.MapGroup("/{itemId}/follow-up").RequireAuthorization(EstimatingPolicies.Editor);
        group.MapPost("", async (int id, string itemId, HttpContext context, QuoteMaterialPurchasingService service,
            [FromServices] MaterialFollowUpDraftQueue queue, CancellationToken ct) =>
        {
            var access = Access(context);
            var draft = await service.GetFollowUpDraftAsync(id, itemId, access, ct);
            return Results.Ok(queue.Enqueue(access.AccountName, draft.Mailbox, id, itemId, draft.SourceMessageId));
        });
        group.MapGet("/{jobId:guid}", (int id, string itemId, Guid jobId, HttpContext context, [FromServices] MaterialFollowUpDraftQueue queue) =>
        {
            var status = queue.Find(jobId, Access(context).AccountName, id, itemId);
            return status is null ? Results.NotFound() : Results.Ok(status);
        });
    }

    internal static void MapMaterialFollowUpConnectorEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/vendor-quotes/follow-up")
            .RequireAuthorization(EstimatingPolicies.QuoteStatusView, EstimatingPolicies.Editor);
        group.AddEndpointFilter(MaterialPurchasingEndpoints.ReviewFilter);

        group.MapPost("/claim", async (ConnectorClaim input, HttpContext context, QuoteMaterialPurchasingService service,
            [FromServices] MaterialFollowUpDraftQueue queue, CancellationToken ct) =>
        {
            var access = Access(context);
            var mailbox = Mailbox(input.Mailbox);
            var job = queue.Claim(access.AccountName, mailbox);
            if (job is null) return Results.Ok(new { job = (ConnectorJob?)null });
            try
            {
                // A reply may have synced after the browser requested the draft.
                var draft = await service.GetFollowUpDraftAsync(job.QuoteId, job.ItemId, access, ct, inspectCurrentRequirements: false);
                if (!string.Equals(draft.Mailbox, mailbox, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(draft.SourceMessageId, job.SourceMessageId, StringComparison.Ordinal))
                    throw new VendorQuoteException(409, "The original RFQ changed. Refresh the material before retrying.");
                return Results.Ok(new { job = queue.IsClaimed(job.Id)
                    ? new ConnectorJob(job.Id, draft.SourceMessageId, draft.ConversationId,
                        draft.Subject, draft.RequiredVendorRecipients, draft.SentAt, draft.Body) : null });
            }
            catch (VendorQuoteException ex)
            {
                queue.Complete(job.Id, access.AccountName, mailbox, false, ex.Message);
                return Results.Ok(new { job = (ConnectorJob?)null });
            }
            catch
            {
                queue.Complete(job.Id, access.AccountName, mailbox, false, "Arda could not recheck this material. Refresh and try again.");
                throw;
            }
        });

        group.MapPost("/result", (ConnectorResult input, HttpContext context, [FromServices] MaterialFollowUpDraftQueue queue) =>
        {
            var access = Access(context);
            var message = input.Opened ? null : input.Message switch
            {
                "follow-up-original-unavailable" => "Outlook could not find the original RFQ in this mailbox's Sent Items.",
                "follow-up-recipient-mismatch" => "The original Outlook message no longer matches the tracked vendor recipients.",
                "follow-up-reply-pending-sync" => "An email in this conversation is waiting to sync. Refresh the material after Outlook sync finishes.",
                _ => "Outlook could not open the Reply All draft. Open Classic Outlook and try again."
            };
            return queue.Complete(input.JobId, access.AccountName, Mailbox(input.Mailbox), input.Opened, message)
                ? Results.NoContent() : Results.NotFound();
        });
    }

    private static EstimatingAccessProfile Access(HttpContext context)
    {
        var access = context.Items[EstimatingPolicies.AccessItem] as EstimatingAccessProfile
            ?? throw new VendorQuoteException(403, "Estimating access is required.");
        VendorQuoteService.Guard(access, write: true);
        return access;
    }

    private static string Mailbox(string? value)
    {
        var address = value?.Trim();
        if (address is null || address.Length > 320 || !MailAddress.TryCreate(address, out var mailbox)
            || !string.Equals(address, mailbox.Address, StringComparison.Ordinal))
            throw new VendorQuoteException(400, "Choose a valid Outlook mailbox.");
        return mailbox.Address;
    }

    private sealed record ConnectorClaim(string? Mailbox);
    private sealed record ConnectorResult(string? Mailbox, Guid JobId, bool Opened, string? Message);
    private sealed record ConnectorJob(Guid Id, string SourceMessageId, string? ConversationId, string Subject,
        IReadOnlyList<string> RecipientEmails, DateTimeOffset OriginalSentAt, string BodyText);
}
