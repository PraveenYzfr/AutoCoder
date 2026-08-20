using System.Security.Cryptography;
using System.Text;
using AutoCoder.Abstractions.Config;

namespace AutoCoder.Core.Webhooks;

/// <summary>HMAC / shared-token check for Jira webhooks. Header preferred; query token still accepted.</summary>
public static class WebhookAuthenticator
{
    public static bool Validate(
        WebhooksOptions webhooks,
        string body,
        string? expectedSecret,
        string? hubSignature,
        string? autoCoderToken,
        string? authorization,
        string? queryToken,
        out string? error)
    {
        error = null;
        var expected = expectedSecret ?? "";

        if (string.IsNullOrEmpty(expected))
        {
            if (webhooks.RequireSecret)
            {
                error = $"Secret env '{webhooks.SecretEnv}' is empty and require_secret=true.";
                return false;
            }

            return true;
        }

        var headerToken = FirstNonEmpty(hubSignature, autoCoderToken, StripBearer(authorization));
        if (!string.IsNullOrEmpty(headerToken))
        {
            if (headerToken.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase))
            {
                var provided = headerToken["sha256=".Length..];
                using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(expected));
                var hash = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(body ?? ""))).ToLowerInvariant();
                if (!FixedEquals(hash, provided.ToLowerInvariant()))
                {
                    error = "HMAC signature mismatch.";
                    return false;
                }

                return true;
            }

            if (!FixedEquals(headerToken, expected))
            {
                error = "Token header mismatch.";
                return false;
            }

            return true;
        }

        if (!string.IsNullOrEmpty(queryToken) && FixedEquals(queryToken, expected))
            return true;

        if (webhooks.RequireSecret)
        {
            error = "Missing webhook secret header/query token.";
            return false;
        }

        return true;
    }

    public static string ComputeSha256Hex(string secret, string body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(body ?? ""))).ToLowerInvariant();
    }

    private static string? StripBearer(string? authorization)
    {
        if (string.IsNullOrWhiteSpace(authorization))
            return authorization;
        return authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authorization["Bearer ".Length..]
            : authorization;
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrEmpty(v));

    private static bool FixedEquals(string a, string b)
    {
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return ba.Length == bb.Length && CryptographicOperations.FixedTimeEquals(ba, bb);
    }
}
