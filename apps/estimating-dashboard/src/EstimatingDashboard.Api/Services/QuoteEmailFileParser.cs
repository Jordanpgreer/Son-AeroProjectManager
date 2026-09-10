using EstimatingDashboard.Api.Dtos;
using MimeKit;
using MimeKit.Text;
using MsgReader.Outlook;
using System.Text;

namespace EstimatingDashboard.Api.Services;

internal sealed record ParsedQuoteEmail(ManualQuoteEmailPreviewDto Preview, string SourceMessageId,
    string? ConversationId, string BodyText, IReadOnlyList<ImportVendorQuoteAttachmentDto> Attachments);

internal static class QuoteEmailFileParser
{
    internal const int MaximumFileBytes = 30 * 1024 * 1024;
    public static ParsedQuoteEmail Parse(string fileName, byte[] content)
    {
        if (content.Length == 0 || content.Length > MaximumFileBytes) throw new VendorQuoteException(400, "Choose an email file up to 30 MB.");
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (extension is not (".eml" or ".msg")) throw new VendorQuoteException(400, "Choose an Outlook .msg or .eml email file.");
        try
        {
            using var input = new MemoryStream(content, false);
            var parsed = extension == ".eml" ? ReadMime(input, fileName) : ReadMsg(input, fileName);
            return parsed with { SourceMessageId = string.IsNullOrWhiteSpace(parsed.SourceMessageId)
                ? "upload:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)) : parsed.SourceMessageId.Trim() };
        }
        catch (VendorQuoteException) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            throw new VendorQuoteException(400, "This email file could not be read. Save the original email as .msg or .eml and try again.");
        }
    }
    private static ParsedQuoteEmail ReadMime(Stream input, string fileName)
    {
        using var message = MimeMessage.Load(input);
        if (message.Body is null || message.Body.ContentType.IsMimeType("application", "pkcs7-mime")
            || message.Body.ContentType.IsMimeType("application", "x-pkcs7-mime")
            || message.Body.ContentType.IsMimeType("multipart", "encrypted"))
            throw new VendorQuoteException(400, "This email is protected or encrypted and cannot be read by this importer.");
        if (message.BodyParts.Any(x => !x.IsAttachment && x.ContentDisposition?.FileName is null && x.ContentType.Name is null
            && (x.ContentType.IsMimeType("application", "pkcs7-mime") || x.ContentType.IsMimeType("application", "x-pkcs7-mime"))))
            throw new VendorQuoteException(400, "This email is protected or encrypted and cannot be read by this importer.");
        if (message.From.Mailboxes.Count() != 1) throw new VendorQuoteException(400, "The email must contain one identifiable sender.");
        var sender = message.From.Mailboxes.Single();
        var recipients = message.To.Mailboxes.Concat(message.Cc.Mailboxes).Concat(message.Bcc.Mailboxes).Select(x => x.Address).ToList();
        var attachments = new List<ImportVendorQuoteAttachmentDto>();
        long total = 0;
        foreach (var part in message.BodyParts.Where(x => x.IsAttachment || x is MimePart { FileName: not null }))
        {
            using var data = new LimitedAttachmentStream();
            if (part is MessagePart { Message: not null } nested) nested.Message.WriteTo(data);
            else if (part is MimePart { Content: not null } mime) mime.Content.DecodeTo(data);
            else throw new VendorQuoteException(400, "An attachment in this email is not supported.");
            AddAttachment(attachments, ref total, part.ContentDisposition?.FileName ?? part.ContentType.Name ?? "attached-email.eml", part.ContentType.MimeType, data.ToArray());
        }
        var body = message.TextBody;
        if (body is null && message.HtmlBody is not null) body = PlainHtml(message.HtmlBody);
        if (body is null)
            throw new VendorQuoteException(400, "This email does not contain a supported readable message body.");
        return Finish(fileName, message.Subject ?? "", sender.Address, sender.Name, recipients, message.Date,
            null, message.Headers[HeaderId.MessageId] ?? "", null, body ?? "", attachments);
    }
    private static ParsedQuoteEmail ReadMsg(Stream input, string fileName)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var message = new Storage.Message(input, FileAccess.Read, true);
        if (message.Type != MessageType.Email && message.Type != MessageType.EmailClearSigned)
            throw new VendorQuoteException(400, "Choose a readable email message. Encrypted emails, appointments, and other Outlook items cannot be imported.");
        if (message.Sender is null) throw new VendorQuoteException(400, "The Outlook file does not contain an email sender.");
        var attachments = new List<ImportVendorQuoteAttachmentDto>();
        long total = 0;
        foreach (var attachment in message.Attachments)
        {
            if (attachment is Storage.Attachment file) AddAttachment(attachments, ref total, file.FileName, file.MimeType, file.Data);
            else if (attachment is Storage.Message nested)
            {
                using var data = new LimitedAttachmentStream(); nested.Save(data);
                AddAttachment(attachments, ref total, (nested.Subject ?? "attached-email") + ".msg", null, data.ToArray());
            }
            else throw new VendorQuoteException(400, "An Outlook attachment type is not supported.");
        }
        var body = message.BodyText;
        if (body is null && message.BodyHtml is not null) body = PlainHtml(message.BodyHtml);
        var sent = message.SentOn ?? message.ReceivedOn ?? throw new VendorQuoteException(400, "The email is missing its sent date.");
        var sourceId = message.Id ?? message.Headers?.MessageId ?? "";
        if (!string.IsNullOrWhiteSpace(sourceId) && !sourceId.Trim().StartsWith('<')) sourceId = "<" + sourceId.Trim() + ">";
        return Finish(fileName, message.Subject ?? "", message.Sender.Email, message.Sender.DisplayName,
            message.Recipients.Select(x => x.Email).ToList(), sent, message.ReceivedOn,
            sourceId, message.ConversationId, body ?? "", attachments);
    }
    private static ParsedQuoteEmail Finish(string file, string subject, string from, string? fromName,
        IReadOnlyList<string> to, DateTimeOffset sent, DateTimeOffset? received, string sourceId, string? conversation,
        string body, List<ImportVendorQuoteAttachmentDto> attachments)
    {
        if (subject.Length > 998 || body.Length > 200000 || to.Count > 100) throw new VendorQuoteException(400, "This email exceeds the supported subject, body, or recipient limits.");
        var fromAddress = VendorQuoteService.Email(from);
        var recipients = to.Select(VendorQuoteService.Email).Distinct().ToList();
        if (sent.Year < 2000 || sent > DateTimeOffset.UtcNow.AddDays(1)) throw new VendorQuoteException(400, "The email sent date is missing or outside the supported range.");
        return new(new(VendorQuoteService.SafeFileName(file), subject, fromAddress,
            VendorQuoteService.Optional(fromName, "Sender name", 200), recipients, sent.ToUniversalTime(), received?.ToUniversalTime(), attachments.Count),
            sourceId, conversation, body, attachments);
    }
    private static string PlainHtml(string html)
    {
        if (html.Length > 2000000) throw new VendorQuoteException(400, "The HTML body is too large to import.");
        // A local tokenizer converts to plain text; it never renders HTML or fetches remote resources.
        using var input = new StringReader(html);
        var tokenizer = new HtmlTokenizer(input) { DecodeCharacterReferences = true };
        var result = new StringBuilder();
        var suppressed = false;
        while (tokenizer.ReadNextToken(out var token))
        {
            if (token is HtmlTagToken tag)
            {
                var name = tag.Name.ToLowerInvariant();
                if (name is "script" or "style" or "head") suppressed = !tag.IsEndTag;
                if (!suppressed && name is "br" or "p" or "div" or "li" or "tr") result.AppendLine();
            }
            else if (!suppressed && token is HtmlDataToken data) result.Append(data.Data);
            if (result.Length > 200000) throw new VendorQuoteException(400, "The email body exceeds 200,000 characters.");
        }
        return result.ToString().Trim();
    }
    private static void AddAttachment(List<ImportVendorQuoteAttachmentDto> files, ref long total, string name, string? type, byte[] bytes)
    {
        total += bytes.Length;
        if (bytes.Length > 10 * 1024 * 1024 || total > 20 * 1024 * 1024 || files.Count >= 25)
            throw new VendorQuoteException(400, "Attachments must be at most 10 MB each, 20 MB combined, and 25 files.");
        files.Add(new(VendorQuoteService.SafeFileName(name), type, Convert.ToBase64String(bytes)));
    }
    private sealed class LimitedAttachmentStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
        private void Check(int count) { if (Position + count > 10 * 1024 * 1024) throw new VendorQuoteException(400, "An attachment exceeds 10 MB."); }
    }
}
