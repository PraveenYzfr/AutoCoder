namespace AutoCoder.Server;

/// <summary>
/// Dashboard is not public. Cloudflare Access (email header) or AUTOCODER_UI_TOKEN.
/// /health and the Jira webhook stay open.
/// </summary>
public static class DashboardAuth
{
    public static bool IsAllowed(HttpRequest request)
    {
        // Access may forward the email, the JWT, or both. Either means the edge already authenticated.
        if (!string.IsNullOrWhiteSpace(request.Headers["Cf-Access-Authenticated-User-Email"].FirstOrDefault())
            || !string.IsNullOrWhiteSpace(request.Headers["Cf-Access-Jwt-Assertion"].FirstOrDefault()))
            return true;

        var expected = Environment.GetEnvironmentVariable("AUTOCODER_UI_TOKEN");
        if (string.IsNullOrWhiteSpace(expected))
            return false;

        var got = request.Headers["X-AutoCoder-Ui-Token"].FirstOrDefault()
                  ?? request.Cookies["autocoder_ui"]
                  ?? (request.Query.TryGetValue("token", out var q) ? q.ToString() : null);
        return string.Equals(got, expected, StringComparison.Ordinal);
    }

    public static void Remember(HttpResponse response, string token)
    {
        response.Cookies.Append("autocoder_ui", token, new CookieOptions
        {
            HttpOnly = true,
            Secure = false,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            MaxAge = TimeSpan.FromDays(7)
        });
    }
}
