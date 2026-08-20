using AutoCoder.Abstractions.Config;
using AutoCoder.Core.Webhooks;

namespace AutoCoder.Tests;

public sealed class WebhookAuthenticatorTests
{
    private static readonly WebhooksOptions Required = new()
    {
        RequireSecret = true,
        SecretEnv = "JIRA_WEBHOOK_SECRET"
    };

    [Fact]
    public void Accepts_valid_hmac()
    {
        const string secret = "test-secret-value-xx";
        const string body = "{\"issue\":{\"key\":\"AC-1\"}}";
        var hex = WebhookAuthenticator.ComputeSha256Hex(secret, body);
        Assert.True(WebhookAuthenticator.Validate(
            Required, body, secret, $"sha256={hex}", null, null, null, out var error));
        Assert.Null(error);
    }

    [Fact]
    public void Rejects_wrong_hmac()
    {
        Assert.False(WebhookAuthenticator.Validate(
            Required, "{}", "secret", "sha256=deadbeef", null, null, null, out var error));
        Assert.Equal("HMAC signature mismatch.", error);
    }

    [Fact]
    public void Rejects_missing_secret_when_required()
    {
        Assert.False(WebhookAuthenticator.Validate(
            Required, "{}", expectedSecret: "", null, null, null, null, out var error));
        Assert.Contains("empty", error);
    }

    [Fact]
    public void Rejects_missing_header_when_required()
    {
        Assert.False(WebhookAuthenticator.Validate(
            Required, "{}", "secret", null, null, null, null, out var error));
        Assert.Contains("Missing webhook secret", error);
    }

    [Fact]
    public void Accepts_shared_token_header()
    {
        Assert.True(WebhookAuthenticator.Validate(
            Required, "{}", "shared-token", null, "shared-token", null, null, out var error));
        Assert.Null(error);
    }

    [Fact]
    public void Timing_safe_compare_does_not_accept_prefix()
    {
        Assert.False(WebhookAuthenticator.Validate(
            Required, "{}", "supersecret", null, "super", null, null, out _));
    }
}
