namespace AutoCoder.Abstractions;

/// <summary>
/// How AutoCoder obtains a GitHub token.
/// Pipeline / RepoHost depend on this — not on PAT vs GitHub App specifics.
/// </summary>
public interface IGitCredentialProvider
{
    /// <summary>pat | github_app | ...</summary>
    string Mode { get; }

    Task<GitCredentials> GetCredentialsAsync(CancellationToken cancellationToken = default);
}

public sealed class GitCredentials
{
    public required string AccessToken { get; init; }

    /// <summary>When set, callers should refresh after this time.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    public string AuthorizationScheme { get; init; } = "Bearer";
}
