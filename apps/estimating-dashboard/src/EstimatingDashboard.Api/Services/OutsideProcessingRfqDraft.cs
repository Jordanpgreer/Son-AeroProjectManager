using System.Globalization;
using System.Net;
using EstimatingDashboard.Api.Dtos;
using MimeKit;

namespace EstimatingDashboard.Api.Services;

internal static class OutsideProcessingRfqDraft
{
    public static MaterialRfqDraftFile Create(int quoteNumber, OutsideProcessingOperationDto operation, string token)
    {
        if (operation.Status != "needs-rfq" || operation.QuoteQuantities.Count == 0
            || operation.VendorEmails.Count == 0 || !MaterialDraftTracking.ValidToken(token))
            throw new VendorQuoteException(409, "This OP is not ready for a vendor draft. Refresh its Fulcrum routing and vendor.");
        var quantities = string.Join(" / ", operation.QuoteQuantities.Select(x =>
            x.ToString("0.############################", CultureInfo.InvariantCulture)));
        var subject = $"Quote {quoteNumber} - {operation.PartNumber}";
        var text = $"Hi team,\r\nPlease quote this outside processing operation with pricing and lead time:\r\n\r\nPart: {operation.PartNumber}\r\nOperation: {operation.Name}\r\nQuantity: {quantities}\r\n"
            + (operation.Instructions is null ? "" : $"Instructions: {operation.Instructions.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n")}\r\n")
            + "\r\nThank you!";
        var html = "<!doctype html><html><body style=\"font-family:Calibri,Arial,sans-serif;font-size:11pt\">"
            + WebUtility.HtmlEncode(text).Replace("\r\n", "<br>") + "</body></html>";
        using var message = new MimeMessage();
        foreach (var email in operation.VendorEmails.Distinct(StringComparer.OrdinalIgnoreCase))
            message.To.Add(new MailboxAddress("", VendorQuoteService.Email(email)));
        message.Subject = subject;
        message.Headers.Add("X-Unsent", "1");
        message.Headers.Add(MaterialDraftTracking.Header, token);
        message.Body = new BodyBuilder { TextBody = text, HtmlBody = html }.ToMessageBody();
        using var output = new MemoryStream();
        var format = FormatOptions.Default.Clone();
        format.NewLineFormat = NewLineFormat.Dos;
        message.WriteTo(format, output);
        var safe = string.Concat((operation.PartNumber + " - " + operation.Name).Select(c =>
            char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ' ' ? c : '_'));
        return new(output.ToArray(), $"Quote {quoteNumber} - {safe[..Math.Min(safe.Length, 120)]}.eml", text);
    }
}
