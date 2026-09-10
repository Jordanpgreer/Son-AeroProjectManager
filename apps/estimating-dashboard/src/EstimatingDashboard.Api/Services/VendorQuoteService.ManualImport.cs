using EstimatingDashboard.Api.Auth;
using EstimatingDashboard.Api.Dtos;
using EstimatingDashboard.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace EstimatingDashboard.Api.Services;

public sealed partial class VendorQuoteService
{
    public async Task<ManualQuoteEmailPreviewDto> PreviewEmailAsync(int quoteId, string fileName, byte[] content,
        EstimatingAccessProfile access, CancellationToken ct)
    {
        await QuoteAsync(quoteId, access, true, ct);
        return QuoteEmailFileParser.Parse(fileName, content).Preview;
    }
    public async Task<VendorQuoteImportResultDto> ImportEmailAsync(int quoteId, string fileName, byte[] content,
        ManualQuoteEmailImportOptions options, EstimatingAccessProfile access, CancellationToken ct)
    {
        var quote = await QuoteAsync(quoteId, access, true, ct);
        var parsed = QuoteEmailFileParser.Parse(fileName, content);
        var email = parsed.Preview;
        var direction = Required(options.Direction, "Direction", 16).ToLowerInvariant();
        string? vendor = options.VendorEmail;
        string? vendorName = null;
        if (options.RequestId.HasValue)
        {
            var thread = await db.Set<VendorQuoteRequest>().SingleOrDefaultAsync(x => x.Id == options.RequestId && x.QuoteHistoryId == quoteId, ct)
                ?? throw new VendorQuoteException(400, "Choose a thread belonging to this quote.");
            vendorName = thread.VendorName;
            if (string.IsNullOrWhiteSpace(vendor) && !string.IsNullOrWhiteSpace(thread.VendorEmail)) vendor = thread.VendorEmail;
        }
        if (string.IsNullOrWhiteSpace(vendor)) vendor = direction == "incoming" ? email.FromAddress
            : email.ToAddresses.Count == 1 ? email.ToAddresses[0]
            : throw new VendorQuoteException(400, "Choose the recipient whose conversation this outgoing email belongs to.");
        if (direction == "incoming") vendorName ??= email.FromName;
        var dto = new ImportVendorQuoteMessageDto(parsed.SourceMessageId, "", direction, email.Subject,
            email.FromAddress, email.FromName, email.ToAddresses, vendor!, vendorName,
            email.SentAt, email.ReceivedAt, parsed.BodyText, parsed.Attachments, parsed.ConversationId);
        return await ImportCoreAsync(dto, access, ct, quoteId, options);
    }
}
