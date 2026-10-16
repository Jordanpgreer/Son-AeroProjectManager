using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Primitives;
using QualityAssurance.Api.Dtos;
using QualityAssurance.Api.Endpoints;

namespace QualityAssurance.Tests;

public sealed class QualityEndpointIntegrityTests
{
    [Fact]
    public void Ajax_guard_requires_the_exact_non_simple_request_header()
    {
        var request = new DefaultHttpContext().Request;

        Assert.False(QualityRequestIntegrity.IsTrustedAjaxRequest(request));
        request.Headers[QualityRequestIntegrity.RequestedWithHeader] = "fetch";
        Assert.False(QualityRequestIntegrity.IsTrustedAjaxRequest(request));
        request.Headers[QualityRequestIntegrity.RequestedWithHeader] = "xmlhttprequest";
        Assert.True(QualityRequestIntegrity.IsTrustedAjaxRequest(request));
    }

    [Fact]
    public void Multipart_guard_requires_multipart_content_and_a_trusted_origin()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("quality.hub.son4l.local");
        context.Request.Headers.Origin = "https://quality.hub.son4l.local";
        Assert.False(QualityRequestIntegrity.IsTrustedMultipartRequest(context.Request));
        context.Request.ContentType = "application/x-www-form-urlencoded";
        Assert.False(QualityRequestIntegrity.IsTrustedMultipartRequest(context.Request));
        context.Request.ContentType = "multipart/form-data; boundary=test";
        Assert.True(QualityRequestIntegrity.IsTrustedMultipartRequest(context.Request));
        context.Request.Headers.Remove("Origin");
        context.Request.Headers[QualityRequestIntegrity.RequestedWithHeader] = QualityRequestIntegrity.RequestedWithValue;
        Assert.False(QualityRequestIntegrity.IsTrustedMultipartRequest(context.Request));
    }

    [Fact]
    public async Task Notification_mutations_reject_untrusted_requests_before_service_access()
    {
        var context = new DefaultHttpContext();

        var one = await QualityCommentEndpoints.MarkNotificationReadAsync(
            42,
            context.Request,
            context,
            null!,
            default);
        var all = await QualityCommentEndpoints.MarkAllNotificationsReadAsync(
            context.Request,
            context,
            null!,
            default);

        Assert.Equal(
            "UntrustedMutationRequest",
            Assert.IsType<ErrorDto>(Assert.IsType<BadRequest<ErrorDto>>(one).Value).Code);
        Assert.Equal(
            "UntrustedMutationRequest",
            Assert.IsType<ErrorDto>(Assert.IsType<BadRequest<ErrorDto>>(all).Value).Code);
    }

    [Fact]
    public async Task Shipment_import_rejects_untrusted_multipart_before_reading_the_workbook()
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = "multipart/form-data; boundary=test";
        var file = new FormFile(Stream.Null, 0, 1, "file", "shipping-status.xlsx");

        var result = await QualityShippingEndpoints.ImportAsync(
            context.Request,
            file,
            context,
            null!,
            null!,
            default);

        Assert.Equal(
            "UntrustedImportRequest",
            Assert.IsType<ErrorDto>(Assert.IsType<BadRequest<ErrorDto>>(result).Value).Code);
    }

    [Theory]
    [InlineData("http://localhost:5140", true)]
    [InlineData("https://hub.son4l.local", true)]
    [InlineData("http://localhost:5170", true)]
    [InlineData("https://untrusted.example", false)]
    [InlineData("https://hub.son4l.local.attacker.example", false)]
    [InlineData("https://hub.son4l.local/path", false)]
    [InlineData("https://hub.son4l.local/", false)]
    [InlineData("https://hub.son4l.local?query=true", false)]
    [InlineData("https://hub.son4l.local#fragment", false)]
    [InlineData("https://user@hub.son4l.local", false)]
    [InlineData("https://hub.son4l.local:444", false)]
    [InlineData("https://hub.son4l.local,https://untrusted.example", false)]
    [InlineData("https://hub.son4l.local https://untrusted.example", false)]
    [InlineData(" https://hub.son4l.local", false)]
    [InlineData("ftp://hub.son4l.local", false)]
    [InlineData("", false)]
    [InlineData("null", false)]
    public void Import_accepts_only_same_origin_or_configured_hub_origins(string origin, bool accepted)
    {
        var request = new DefaultHttpContext().Request;
        request.Scheme = "http";
        request.Host = new HostString("localhost", 5170);
        request.ContentType = "multipart/form-data; boundary=test";
        request.Headers[QualityRequestIntegrity.RequestedWithHeader] = QualityRequestIntegrity.RequestedWithValue;
        request.Headers.Origin = origin;

        Assert.Equal(accepted, QualityRequestIntegrity.IsTrustedMultipartRequest(request,
            ["http://localhost:5140", "https://hub.son4l.local"]));
        request.Headers.Remove(QualityRequestIntegrity.RequestedWithHeader);
        Assert.Equal(accepted, QualityRequestIntegrity.IsTrustedMultipartRequest(request,
            ["http://localhost:5140", "https://hub.son4l.local"]));
    }

    [Fact]
    public void Multipart_import_rejects_multiple_origin_values_and_wildcard_configuration()
    {
        var request = new DefaultHttpContext().Request;
        request.Scheme = "https";
        request.Host = new HostString("quality.hub.son4l.local");
        request.ContentType = "multipart/form-data; boundary=test";
        request.Headers.Origin = new StringValues(["https://hub.son4l.local", "https://hub.son4l.local"]);
        Assert.False(QualityRequestIntegrity.IsTrustedMultipartRequest(request, ["https://hub.son4l.local"]));
        request.Headers.Origin = "https://untrusted.example";
        Assert.False(QualityRequestIntegrity.IsTrustedMultipartRequest(request, ["*"]));
    }

    [Theory]
    [InlineData("https://hub.son4l.local", "EmptyWorkbook")]
    [InlineData("https://untrusted.example", "UntrustedImportRequest")]
    [InlineData("null", "UntrustedImportRequest")]
    public async Task Headerless_import_uses_the_exact_hub_origin_policy_before_file_validation(string origin, string expectedCode)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("quality.hub.son4l.local");
        context.Request.ContentType = "multipart/form-data; boundary=test";
        context.Request.Headers.Origin = origin;
        var file = new FormFile(Stream.Null, 0, 0, "file", "shipping-status.xlsx");

        var result = await QualityShippingEndpoints.ImportAsync(context.Request, file, context, null!,
            new TestCorsPolicyProvider(), default);

        Assert.Equal(expectedCode, Assert.IsType<ErrorDto>(Assert.IsType<BadRequest<ErrorDto>>(result).Value).Code);
    }

    private sealed class TestCorsPolicyProvider : ICorsPolicyProvider
    {
        public Task<CorsPolicy?> GetPolicyAsync(HttpContext context, string? policyName)
        {
            Assert.Equal("HubAdmin", policyName);
            return Task.FromResult<CorsPolicy?>(new CorsPolicyBuilder()
                .WithOrigins("https://hub.son4l.local").AllowCredentials().Build());
        }
    }
}
