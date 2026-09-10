using MimeKit;
using OpenMcdf;
using System.Text;
using static EstimatingDashboard.Tests.VendorQuoteTestFixture;

namespace EstimatingDashboard.Tests;

internal static class ManualEmailFixtures
{
    internal static byte[] Eml(string subject = "Wrong customer subject", bool outgoing = false, bool html = false, string? messageId = "manual-test@vendor.example")
    {
        using var message = new MimeMessage();
        message.From.Add(new MailboxAddress(outgoing ? "Casey Lee" : "Silicone Prime", outgoing ? "casey@company.example" : "quotes@vendor.example"));
        message.To.Add(MailboxAddress.Parse(outgoing ? "quotes@vendor.example" : "casey@company.example"));
        if (outgoing) message.Cc.Add(MailboxAddress.Parse("other@vendor.example"));
        message.Subject = subject; message.Date = Now; if (messageId is not null) message.MessageId = messageId;
        var builder = new BodyBuilder();
        if (html) builder.HtmlBody = "<html><head><style>body{color:red}</style></head><body><p>Vendor pricing &amp; details</p><script>secretScript()</script><img src='https://example.invalid/never-fetch'></body></html>";
        else builder.TextBody = "Synthetic vendor pricing details";
        builder.Attachments.Add("rates.txt", "Synthetic rates"u8.ToArray(), new ContentType("text", "plain"));
        message.Body = builder.ToMessageBody();
        using var stream = new MemoryStream(); message.WriteTo(stream); return stream.ToArray();
    }
    internal static byte[] Msg(string subject = "Incorrect Outlook subject", string messageClass = "IPM.Note", bool exchange = false)
    {
        using var output = new MemoryStream();
        using (var root = RootStorage.Create(output, OpenMcdf.Version.V3, StorageModeFlags.LeaveOpen))
        {
            String(root, "001A", messageClass); String(root, "0037", subject);
            String(root, "0C1A", "Silicone Prime"); String(root, "0C1E", exchange ? "EX" : "SMTP");
            String(root, "0C1F", exchange ? "/o=Company/ou=Exchange/cn=Recipients/cn=vendor" : "quotes@vendor.example");
            String(root, "5D01", "quotes@vendor.example");
            String(root, "1000", "Synthetic MSG rates are attached."); String(root, "1035", "<msg-test@vendor.example>");
            using (var properties = root.CreateStream("__properties_version1.0"))
            {
                properties.Write(new byte[32]);
                using var writer = new BinaryWriter(properties, Encoding.UTF8, true);
                writer.Write(0x00390040u); writer.Write(0u); writer.Write(Now.UtcDateTime.ToFileTimeUtc());
                writer.Write(0x0E060040u); writer.Write(0u); writer.Write(Now.UtcDateTime.ToFileTimeUtc());
            }
            var recipient = root.CreateStorage("__recip_version1.0_#00000000");
            String(recipient, "3001", "Casey Lee"); String(recipient, "3002", "SMTP"); String(recipient, "3003", "casey@company.example");
            using (var properties = recipient.CreateStream("__properties_version1.0"))
            {
                properties.Write(new byte[8]); using var writer = new BinaryWriter(properties, Encoding.UTF8, true);
                writer.Write(0x0C150003u); writer.Write(0u); writer.Write(1); writer.Write(0);
            }
            var attachment = root.CreateStorage("__attach_version1.0_#00000000");
            String(attachment, "3707", "rates.txt"); String(attachment, "3704", "rates.txt"); String(attachment, "370E", "text/plain");
            using (var data = attachment.CreateStream("__substg1.0_37010102")) data.Write("Synthetic MSG rates"u8);
            using (var properties = attachment.CreateStream("__properties_version1.0"))
            {
                properties.Write(new byte[8]); using var writer = new BinaryWriter(properties, Encoding.UTF8, true);
                writer.Write(0x37050003u); writer.Write(0u); writer.Write(1); writer.Write(0);
            }
            root.Flush();
        }
        return output.ToArray();
    }
    private static void String(Storage storage, string tag, string value)
    {
        using var data = storage.CreateStream($"__substg1.0_{tag}001F"); data.Write(Encoding.Unicode.GetBytes(value + '\0'));
    }
}
