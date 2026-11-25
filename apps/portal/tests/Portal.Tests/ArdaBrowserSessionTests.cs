using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SonAero.Platform.Security;

namespace Portal.Tests;

public sealed class ArdaBrowserSessionTests
{
    [Fact]
    public async Task Logout_is_shared_across_the_production_hosts_until_explicit_sign_in()
    {
        using var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .BuildServiceProvider();
        var app = new ApplicationBuilder(services);
        app.UseArdaBrowserSession();
        app.Run(context => context.Response.WriteAsync("Application"));
        var pipeline = app.Build();

        var logout = Request(services, "quality.hub.son4l.local", "/arda-session/logout", "POST");
        logout.Request.Headers.Origin = "https://quality.hub.son4l.local";
        await pipeline(logout);
        Assert.Equal(303, logout.Response.StatusCode);
        Assert.Equal("/arda-session/signed-out", logout.Response.Headers.Location.ToString());
        var cookie = logout.Response.Headers.SetCookie.ToString();
        Assert.Contains("domain=hub.son4l.local", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);

        var signedOut = Request(services, "estimating.hub.son4l.local", "/api/me");
        signedOut.Request.Headers.Cookie = cookie.Split(';')[0];
        await pipeline(signedOut);
        Assert.Equal(403, signedOut.Response.StatusCode);
        Assert.False(signedOut.Response.Headers.ContainsKey("WWW-Authenticate"));

        var navigation = Request(services, "estimating.hub.son4l.local", "/");
        navigation.Request.Headers.Cookie = cookie.Split(';')[0];
        navigation.Request.Headers.Accept = "text/html";
        await pipeline(navigation);
        Assert.Equal("/arda-session/signed-out", navigation.Response.Headers.Location.ToString());

        var signIn = Request(services, "estimating.hub.son4l.local", "/arda-session/sign-in", "POST");
        signIn.Request.Headers.Cookie = cookie.Split(';')[0];
        signIn.Request.Headers.Origin = "https://estimating.hub.son4l.local";
        await pipeline(signIn);
        Assert.Equal(303, signIn.Response.StatusCode);
        Assert.Equal("/", signIn.Response.Headers.Location.ToString());
        Assert.Contains("domain=hub.son4l.local", signIn.Response.Headers.SetCookie.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("expires=", signIn.Response.Headers.SetCookie.ToString(), StringComparison.OrdinalIgnoreCase);

        var resumed = Request(services, "hub.son4l.local", "/api/me");
        await pipeline(resumed);
        Assert.Equal(200, resumed.Response.StatusCode);
    }

    [Theory]
    [InlineData("GET", "https://hub.son4l.local", 405)]
    [InlineData("POST", "https://quality.hub.son4l.local", 403)]
    [InlineData("POST", null, 403)]
    public async Task Logout_requires_a_same_origin_post(string method, string? origin, int status)
    {
        using var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .BuildServiceProvider();
        var app = new ApplicationBuilder(services);
        app.UseArdaBrowserSession();
        var context = Request(services, "hub.son4l.local", "/arda-session/logout", method);
        if (origin is not null) context.Request.Headers.Origin = origin;

        await app.Build()(context);

        Assert.Equal(status, context.Response.StatusCode);
        Assert.False(context.Response.Headers.ContainsKey("Set-Cookie"));
    }

    private static DefaultHttpContext Request(IServiceProvider services, string host, string path, string method = "GET")
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Scheme = "https";
        context.Request.Host = new HostString(host);
        context.Request.Path = path;
        context.Request.Method = method;
        context.Response.Body = new MemoryStream();
        return context;
    }
}
