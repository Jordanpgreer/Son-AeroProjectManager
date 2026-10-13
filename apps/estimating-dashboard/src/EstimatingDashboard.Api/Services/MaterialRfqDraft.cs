using System.Globalization;
using System.Net;
using System.Text;
using EstimatingDashboard.Api.Dtos;
using MimeKit;

namespace EstimatingDashboard.Api.Services;

internal sealed record MaterialRfqDraftFile(byte[] Content, string FileName, string? PlainText = null);

internal static class MaterialRfqDraft
{
    internal static MaterialRfqDraftFile Create(int quoteNumber, MaterialRequirementDto material, string? trackingToken = null)
    {
        if (material.Status != "needs-rfq" || material.QuoteQuantities.Count == 0
            || material.QuoteQuantities.Any(value => value <= 0)
            || string.IsNullOrWhiteSpace(material.PartNumber) || material.PartNumber.Any(char.IsControl)
            || string.IsNullOrWhiteSpace(material.Description)
            || string.IsNullOrWhiteSpace(material.UnitOfMeasure) || material.UnitOfMeasure.Any(char.IsControl)
            || material.VendorEmails.Count == 0)
            throw new VendorQuoteException(409, "This material is not ready for a vendor draft. Refresh and review its requirements.");

        var quantity = string.Join(", ", material.QuoteQuantities.Distinct().Order()
            .Select(value => value.ToString("0.############################", CultureInfo.InvariantCulture)))
            + " " + material.UnitOfMeasure + " OR MOQ";
        var description = material.Description.Replace("\r\n", "\n").Replace('\r', '\n');
        var subject = $"Quote {quoteNumber} - {material.PartNumber}";
        var text = $"Hi team,\r\nPlease quote the following with pricing and lead time:\r\n\r\n{material.PartNumber}\r\n{description.Replace("\n", "\r\n")}\r\n\r\nQTY: {quantity}\r\n\r\nThank you!";
        static string Html(string value) => WebUtility.HtmlEncode(value).Replace("\n", "<br>");
        var html = "<!doctype html><html><body style=\"font-family:Calibri,Arial,sans-serif;font-size:11pt\">"
            + "Hi team,<br>Please quote the following with pricing and lead time:<br><br>"
            + $"<strong>{Html(material.PartNumber)}</strong><br>{Html(description)}<br><br>"
            + $"QTY: {Html(quantity)}<br><br>Thank you!</body></html>";

        using var message = new MimeMessage();
        foreach (var email in material.VendorEmails.Distinct(StringComparer.OrdinalIgnoreCase))
            message.To.Add(new MailboxAddress("", VendorQuoteService.Email(email)));
        message.Subject = subject;
        message.Headers.Add("X-Unsent", "1");
        if (trackingToken is not null)
        {
            if (!MaterialDraftTracking.ValidToken(trackingToken)) throw new InvalidOperationException("Invalid material draft tracking token.");
            message.Headers.Add(MaterialDraftTracking.Header, trackingToken);
        }
        message.Body = new BodyBuilder { TextBody = text, HtmlBody = html }.ToMessageBody();
        // Outlook selects the user's sending account when this unsent message is opened.
        // No From address or sender identity is inferred from the Windows login.
        using var output = new MemoryStream();
        var format = FormatOptions.Default.Clone();
        format.NewLineFormat = NewLineFormat.Dos;
        message.WriteTo(format, output);
        var safePart = string.Concat(material.PartNumber.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'));
        return new(output.ToArray(), $"Quote {quoteNumber} - {safePart[..Math.Min(safePart.Length, 120)]}.eml", text);
    }
}
