using EstimatingDashboard.Api.Auth;
using EstimatingDashboard.Api.Dtos;
using EstimatingDashboard.Api.Services;

namespace EstimatingDashboard.Api.Endpoints;

public static class OutlookConnectorEndpoints
{
    public static RouteGroupBuilder MapOutlookConnectorEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/vendor-quotes/connector", (
            HttpContext context, IConfiguration configuration, IWebHostEnvironment environment) =>
        {
            var access = context.Items[EstimatingPolicies.AccessItem] as EstimatingAccessProfile;
            if (access is null || access.IsPreview) return Results.Forbid();

            // Do not derive the destination of a Windows-authenticated connector from a Host header.
            if (!OutlookConnectorPackage.TryGetDestination(configuration["OutlookConnector:BaseUrl"], out var address))
                return Results.Problem("The Outlook connector destination must be a configured HTTPS address.", statusCode: 503);

            var directory = Path.Combine(environment.ContentRootPath, "Assets", "OutlookConnector");
            if (!Directory.Exists(directory)) directory = Path.Combine(AppContext.BaseDirectory, "Assets", "OutlookConnector");
            if (!OutlookConnectorPackage.IsAvailable(directory))
                return Results.Json(new ErrorDto("ConnectorUnavailable", "The Outlook connector package is not included in this installation."), statusCode: 503);

            context.Response.Headers.CacheControl = "no-store";
            return Results.File(OutlookConnectorPackage.Create(directory, address!), "application/zip", "Arda-Outlook-Connector.zip");
        }).RequireAuthorization(EstimatingPolicies.QuoteStatusView, EstimatingPolicies.Editor);
        return api;
    }
}
