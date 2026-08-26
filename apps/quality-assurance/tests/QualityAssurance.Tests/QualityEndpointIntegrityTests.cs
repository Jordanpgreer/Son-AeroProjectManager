using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
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
    public void Multipart_guard_requires_both_form_content_and_the_ajax_header()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[QualityRequestIntegrity.RequestedWithHeader] =
            QualityRequestIntegrity.RequestedWithValue;

        Assert.False(QualityRequestIntegrity.IsTrustedMultipartAjaxRequest(context.Request));
        context.Request.ContentType = "multipart/form-data; boundary=test";
        Assert.True(QualityRequestIntegrity.IsTrustedMultipartAjaxRequest(context.Request));
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
            default);

        Assert.Equal(
            "UntrustedImportRequest",
            Assert.IsType<ErrorDto>(Assert.IsType<BadRequest<ErrorDto>>(result).Value).Code);
    }
}
