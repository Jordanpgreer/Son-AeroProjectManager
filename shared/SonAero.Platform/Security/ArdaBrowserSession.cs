using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace SonAero.Platform.Security;

public static class ArdaBrowserSession
{
    private const string CookieName = "arda.signed-out";
    private const string SessionPath = "/arda-session";
    private const string SignedOutPath = SessionPath + "/signed-out";

    public static IApplicationBuilder UseArdaBrowserSession(this IApplicationBuilder app)
    {
        var cookieDomain = app.ApplicationServices.GetRequiredService<IConfiguration>()
            ["Authentication:ArdaSignOutDomain"] ?? "hub.son4l.local";
        return app.Use(async (context, next) =>
        {
            var request = context.Request;
            var response = context.Response;
            var signedOut = request.Cookies.ContainsKey(CookieName);

            if (request.Path.StartsWithSegments(SessionPath))
            {
                response.Headers.CacheControl = "no-store";
                if (request.Path == SessionPath + "/status" && HttpMethods.IsHead(request.Method))
                {
                    response.StatusCode = signedOut ? StatusCodes.Status403Forbidden : StatusCodes.Status204NoContent;
                    if (signedOut) response.Headers["X-Arda-Signed-Out"] = "1";
                    return;
                }

                if (request.Path == SignedOutPath && HttpMethods.IsGet(request.Method))
                {
                    if (!signedOut)
                    {
                        response.Redirect(request.PathBase.Add("/").ToUriComponent());
                        return;
                    }
                    await WriteSignedOutPageAsync(context);
                    return;
                }

                var isLogout = request.Path == SessionPath + "/logout";
                if (isLogout || request.Path == SessionPath + "/sign-in")
                {
                    if (!HttpMethods.IsPost(request.Method))
                    {
                        response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                        response.Headers.Allow = HttpMethods.Post;
                        return;
                    }
                    if (!HasSameOrigin(request))
                    {
                        response.StatusCode = StatusCodes.Status403Forbidden;
                        return;
                    }

                    var cookie = CreateCookieOptions(request, cookieDomain);
                    if (isLogout)
                    {
                        cookie.MaxAge = TimeSpan.FromDays(30);
                        response.Cookies.Append(CookieName, "1", cookie);
                    }
                    else
                    {
                        response.Cookies.Delete(CookieName, cookie);
                    }
                    response.StatusCode = StatusCodes.Status303SeeOther;
                    response.Headers.Location = request.PathBase.Add(isLogout ? SignedOutPath : "/").ToUriComponent();
                    return;
                }

                response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            if (!signedOut || request.Path == "/api/health")
            {
                await next(context);
                return;
            }

            response.Headers.CacheControl = "no-store";
            if (HttpMethods.IsGet(request.Method)
                && !request.Path.StartsWithSegments("/api")
                && request.GetTypedHeaders().Accept.Any(value => value.MediaType == "text/html"))
            {
                response.Redirect(request.PathBase.Add(SignedOutPath).ToUriComponent());
                return;
            }

            // Avoid an authentication challenge, which would silently sign the browser back in.
            response.StatusCode = StatusCodes.Status403Forbidden;
            await response.WriteAsJsonAsync(new { message = "You are logged out of Arda. Sign in to continue." });
        });
    }

    private static bool HasSameOrigin(HttpRequest request)
    {
        var origin = request.Headers.Origin;
        return origin.Count == 1 && string.Equals(
            origin[0], $"{request.Scheme}://{request.Host.ToUriComponent()}", StringComparison.OrdinalIgnoreCase);
    }

    private static CookieOptions CreateCookieOptions(HttpRequest request, string domain)
    {
        var host = request.Host.Host;
        var shareAcrossModules = request.IsHttps && !string.IsNullOrWhiteSpace(domain)
            && (host.Equals(domain, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));
        return new CookieOptions
        {
            Domain = shareAcrossModules ? domain : null,
            Path = "/",
            HttpOnly = true,
            Secure = request.IsHttps,
            SameSite = SameSiteMode.Lax,
            IsEssential = true
        };
    }

    private static Task WriteSignedOutPageAsync(HttpContext context)
    {
        var action = HtmlEncoder.Default.Encode(context.Request.PathBase.Add(SessionPath + "/sign-in").ToUriComponent());
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'";
        return context.Response.WriteAsync($$"""
            <!doctype html>
            <html lang="en">
            <head>
                <meta charset="utf-8">
                <meta name="viewport" content="width=device-width, initial-scale=1">
                <title>Logged out · Arda</title>
                <style>
                    :root { color-scheme: light dark; font: 1rem system-ui, sans-serif; }
                    body { min-height: 90vh; display: grid; place-items: center; }
                    main { max-width: 28rem; padding: 2rem; }
                    button { font: inherit; padding: .65rem 1rem; cursor: pointer; }
                </style>
            </head>
            <body><main>
                <h1>Logged out of Arda</h1>
                <p>Sign in with your Windows account to return to Arda.</p>
                <form method="post" action="{{action}}"><button type="submit">Sign in with Windows</button></form>
            </main></body>
            </html>
            """);
    }
}
