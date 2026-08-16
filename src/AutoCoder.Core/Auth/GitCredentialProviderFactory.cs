using AutoCoder.Abstractions;
using AutoCoder.Abstractions.Config;

namespace AutoCoder.Core.Auth;

public static class GitCredentialProviderFactory
{
    /// <summary>
    /// Selects credential source from env/config without the pipeline knowing which one.
    /// GITHUB_AUTH_MODE=pat|github_app (default: pat)
    /// </summary>
    public static IGitCredentialProvider Create(AutoCoderOptions? options = null)
    {
        var mode = Environment.GetEnvironmentVariable("GITHUB_AUTH_MODE")
            ?? options?.Secrets.GetValueOrDefault("github_auth_mode")
            ?? "pat";

        mode = mode.Trim().ToLowerInvariant();
        return mode switch
        {
            "pat" or "token" or "personal" => new PatGitCredentialProvider(),
            "github_app" or "app" => new GitHubAppCredentialProvider(),
            _ => throw new InvalidOperationException(
                $"Unknown GITHUB_AUTH_MODE '{mode}'. Use 'pat' or 'github_app'.")
        };
    }
}
