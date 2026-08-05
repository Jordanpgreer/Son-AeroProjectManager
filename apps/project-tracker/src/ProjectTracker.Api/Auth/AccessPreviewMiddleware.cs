using System.Security.Claims;
using SonAero.Platform.Security;

namespace ProjectTracker.Api.Auth;

public sealed class AccessPreviewMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        ProjectTrackerAccessPreviewService previewService)
    {
        if (HttpMethods.IsPost(context.Request.Method)
            && context.Request.Path.Equals("/access-preview/start", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        if (HttpMethods.IsGet(context.Request.Method)
            && context.Request.Path == "/"
            && context.Request.Query.ContainsKey("launch")
            && previewService.HasPreviewCookie(context.Request))
        {
            await previewService.RevokeAsync(context.User, context.Request, context.RequestAborted);
            previewService.ClearCookie(context.Response, context.Request.IsHttps);
            await next(context);
            return;
        }

        var previewClaim = context.User.HasClaim(AccessPreviewClaimTypes.Active, "true");
        if ((previewClaim || previewService.HasPreviewCookie(context.Request))
            && !AccessPreviewRequests.IsReadOnlyMethod(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new
            {
                code = "AccessPreviewReadOnly",
                message = "Access preview is read-only. Return to Hub Admin to make changes."
            }, context.RequestAborted);
            return;
        }

        await next(context);
    }
}

public static class AccessPreviewEndpoints
{
    public static IEndpointRouteBuilder MapAccessPreviewEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/access-preview/start", async (
            HttpContext context,
            ProjectTrackerAccessPreviewService previewService) =>
        {
            if (!context.Request.HasFormContentType)
            {
                return Results.BadRequest("A preview token is required.");
            }

            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var result = await previewService.RedeemAsync(context.User, form["token"].ToString(), context.RequestAborted);
            if (!result.Succeeded || result.SessionToken is null || result.SessionExpiresAt is null)
            {
                return Results.BadRequest(result.Error ?? "The preview launch could not be started.");
            }

            previewService.SetCookie(
                context.Response,
                result.SessionToken,
                result.SessionExpiresAt.Value,
                context.Request.IsHttps);
            return Results.Redirect("/");
        }).RequireAuthorization();

        endpoints.MapGet("/access-preview/end", async (
            HttpContext context,
            ProjectTrackerAccessPreviewService previewService) =>
        {
            await previewService.RevokeAsync(context.User, context.Request, context.RequestAborted);
            previewService.ClearCookie(context.Response, context.Request.IsHttps);
            return Results.Redirect(previewService.HubAccessAdminUrl(context.Request));
        }).RequireAuthorization();

        return endpoints;
    }
}
