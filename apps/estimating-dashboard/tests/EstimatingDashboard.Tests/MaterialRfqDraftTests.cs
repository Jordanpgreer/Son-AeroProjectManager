using EstimatingDashboard.Api.Auth;
using EstimatingDashboard.Api.Dtos;
using EstimatingDashboard.Api.Endpoints;
using EstimatingDashboard.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;

namespace EstimatingDashboard.Tests;

public sealed class MaterialRfqDraftTests
{
    internal static MaterialRequirementDto Material => new("material-1", "RM004420",
        "LEXAN 101-701 BLACK #37038 REF 13056590-003 QPL", "Pound", 15, 15, 5,
        [15, 22.5m], "needs-rfq", "vendor-1", "NEXEO PLASTICS, LLC",
        ["cheri@example.test", "mark@example.test"], null,
        [new("11488049", "11488049", 10, .005m, 15, 15)], []);

    [Fact]
    public void Draft_has_exact_body_full_requirements_all_contacts_bold_part_and_unsent_header()
    {
        var file = MaterialRfqDraft.Create(4445, Material);
        using var message = MimeMessage.Load(new MemoryStream(file.Content));
        Assert.Equal("Quote 4445 - RM004420", message.Subject);
        Assert.Equal("1", message.Headers["X-Unsent"]);
        Assert.Equal(new[] { "cheri@example.test", "mark@example.test" }, message.To.Mailboxes.Select(x => x.Address));
        Assert.Empty(message.From);
        Assert.Equal("Hi team,\nPlease quote the following with pricing and lead time:\n\nRM004420\nLEXAN 101-701 BLACK #37038 REF 13056590-003 QPL\n\nQTY: 15, 22.5 Pound OR MOQ\n\nThank you!",
            message.TextBody!.Replace("\r\n", "\n"));
        Assert.Contains("<strong>RM004420</strong>", message.HtmlBody);
        Assert.Equal("Quote 4445 - RM004420.eml", file.FileName);
    }

    [Fact]
    public void Html_escapes_description_and_preserves_lines_without_adding_notes()
    {
        var material = Material with { Description = "First <script>alert('x')</script> & line\r\nSecond line", QuoteQuantities = [25, 15, 25] };
        using var message = MimeMessage.Load(new MemoryStream(MaterialRfqDraft.Create(4445, material).Content));
        Assert.DoesNotContain("<script>", message.HtmlBody);
        Assert.Contains("&lt;script&gt;", message.HtmlBody);
        Assert.Contains("line<br>Second line", message.HtmlBody);
        Assert.Contains("QTY: 15, 25 Pound OR MOQ", message.TextBody);
    }

    [Theory]
    [InlineData("stock-covered")]
    [InlineData("recent-po")]
    [InlineData("review-required")]
    public void Non_actionable_materials_cannot_create_drafts(string status) =>
        Assert.Throws<VendorQuoteException>(() => MaterialRfqDraft.Create(4445, Material with { Status = status }));

    [Fact]
    public void Untrusted_headers_and_missing_required_fields_cannot_create_drafts()
    {
        foreach (var material in new[] {
            Material with { VendorEmails = ["sales@example.test\r\nBcc: hidden@example.test"] },
            Material with { PartNumber = "PART\r\nBcc: hidden@example.test" },
            Material with { Description = " " }, Material with { VendorEmails = [] },
            Material with { QuoteQuantities = [0] }, Material with { UnitOfMeasure = "" } })
            Assert.Throws<VendorQuoteException>(() => MaterialRfqDraft.Create(4445, material));
    }

    [Fact]
    public void Routes_require_quote_visibility_and_draft_requires_editor()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddScoped<QuoteMaterialPurchasingService>();
        var app = builder.Build();
        app.MapGroup("/api").MapMaterialPurchasingEndpoints();
        var routes = ((IEndpointRouteBuilder)app).DataSources.SelectMany(x => x.Endpoints).OfType<RouteEndpoint>().ToList();
        Assert.Equal(3, routes.Count);
        Assert.All(routes, endpoint => Assert.Contains(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(),
            policy => policy.Policy == EstimatingPolicies.QuoteStatusView));
        var draft = routes.Single(x => x.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Contains("POST"));
        Assert.Contains(draft.Metadata.GetOrderedMetadata<IAuthorizeData>(), policy => policy.Policy == EstimatingPolicies.Editor);
        var quantities = routes.Single(x => x.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Contains("PUT"));
        Assert.Contains(quantities.Metadata.GetOrderedMetadata<IAuthorizeData>(), policy => policy.Policy == EstimatingPolicies.Editor);
    }

    [Fact]
    public void Cross_site_form_posts_cannot_prepare_or_update_material_drafts()
    {
        var request = new Microsoft.AspNetCore.Http.DefaultHttpContext().Request;
        request.Method = "POST";
        request.ContentType = "application/x-www-form-urlencoded";
        Assert.False(MaterialPurchasingEndpoints.HasWriteHeader(request));
        request.Headers["X-Arda-Request"] = "wrong";
        Assert.False(MaterialPurchasingEndpoints.HasWriteHeader(request));
        request.Headers["X-Arda-Request"] = "vendor-quotes";
        Assert.True(MaterialPurchasingEndpoints.HasWriteHeader(request));
    }
}
