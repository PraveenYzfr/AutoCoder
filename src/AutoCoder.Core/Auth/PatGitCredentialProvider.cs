using AutoCoder.Abstractions;

namespace AutoCoder.Core.Auth;

/// <summary>Local/dev: long-lived personal or machine PAT from env.</summary>
public sealed class PatGitCredentialProvider : IGitCredentialProvider
{
    private readonly string _token;

    public string Mode => "pat";

    public PatGitCredentialProvider(string? token = null)
    {
        _token = token
            ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN")
            ?? throw new InvalidOperationException(
                "GITHUB_TOKEN is not set. For auth mode 'pat', put a PAT in .env (never commit it).");
    }

    public Task<GitCredentials> GetCredentialsAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new GitCredentials
        {
            AccessToken = _token,
            ExpiresAt = null
        });
    }
}
