using AutoCoder.Server;
using Microsoft.AspNetCore.Http;

namespace AutoCoder.Tests;

[Collection(ProcessEnvStateCollection.Name)]
public sealed class DashboardAuthTests : IDisposable
{
    private readonly string? _prevToken = Environment.GetEnvironmentVariable("AUTOCODER_UI_TOKEN");

    [Fact]
    public void Denies_when_no_token_configured_and_no_access_headers()
    {
        Environment.SetEnvironmentVariable("AUTOCODER_UI_TOKEN", null);
        var request = new DefaultHttpContext().Request;
        Assert.False(DashboardAuth.IsAllowed(request));
    }

    [Fact]
    public void Allows_when_cloudflare_access_email_header_present()
    {
        Environment.SetEnvironmentVariable("AUTOCODER_UI_TOKEN", null);
        var context = new DefaultHttpContext();
        context.Request.Headers["Cf-Access-Authenticated-User-Email"] = "praveen@example.com";
        Assert.True(DashboardAuth.IsAllowed(context.Request));
    }

    [Fact]
    public void Allows_when_cloudflare_access_jwt_present_even_without_email()
    {
        Environment.SetEnvironmentVariable("AUTOCODER_UI_TOKEN", null);
        var context = new DefaultHttpContext();
        context.Request.Headers["Cf-Access-Jwt-Assertion"] = "some.jwt.token";
        Assert.True(DashboardAuth.IsAllowed(context.Request));
    }

    [Fact]
    public void Allows_matching_ui_token_via_header()
    {
        Environment.SetEnvironmentVariable("AUTOCODER_UI_TOKEN", "secret123");
        var context = new DefaultHttpContext();
        context.Request.Headers["X-AutoCoder-Ui-Token"] = "secret123";
        Assert.True(DashboardAuth.IsAllowed(context.Request));
    }

    [Fact]
    public void Allows_matching_ui_token_via_query_string()
    {
        Environment.SetEnvironmentVariable("AUTOCODER_UI_TOKEN", "secret123");
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?token=secret123");
        Assert.True(DashboardAuth.IsAllowed(context.Request));
    }

    [Fact]
    public void Allows_matching_ui_token_via_cookie()
    {
        Environment.SetEnvironmentVariable("AUTOCODER_UI_TOKEN", "secret123");
        var context = new DefaultHttpContext();
        context.Request.Headers["Cookie"] = "autocoder_ui=secret123";
        Assert.True(DashboardAuth.IsAllowed(context.Request));
    }

    [Fact]
    public void Rejects_wrong_ui_token()
    {
        Environment.SetEnvironmentVariable("AUTOCODER_UI_TOKEN", "secret123");
        var context = new DefaultHttpContext();
        context.Request.Headers["X-AutoCoder-Ui-Token"] = "wrong-guess";
        Assert.False(DashboardAuth.IsAllowed(context.Request));
    }

    [Fact]
    public void Remember_sets_an_httponly_cookie_that_is_then_accepted()
    {
        Environment.SetEnvironmentVariable("AUTOCODER_UI_TOKEN", "secret123");
        var context = new DefaultHttpContext();
        DashboardAuth.Remember(context.Response, "secret123");

        var setCookie = context.Response.Headers.SetCookie.ToString();
        Assert.Contains("autocoder_ui=secret123", setCookie);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => Environment.SetEnvironmentVariable("AUTOCODER_UI_TOKEN", _prevToken);
}
