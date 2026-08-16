namespace AutoCoder.Abstractions;

public sealed class PullRequestRequest
{
    public required string RepoUrl { get; init; }
    public required string HeadBranch { get; init; }
    public required string BaseBranch { get; init; }
    public required string Title { get; init; }
    public required string Body { get; init; }
    public bool Draft { get; init; }
}

public sealed class PullRequestResult
{
    public required string Url { get; init; }
    public int? Number { get; init; }
    public bool DryRun { get; init; }
}

public sealed class CommitRequest
{
    public required string RepoUrl { get; init; }
    public required string Branch { get; init; }
    public required string Message { get; init; }
    public string? WorkDirectory { get; init; }
}

/// <summary>Git host operations. v1 opens PRs only — never merges.</summary>
public interface IRepoHost
{
    Task EnsureAllowlistedAsync(string repoUrl, CancellationToken cancellationToken = default);

    /// <summary>Clone (or reuse) repo into workDirectory and create/checkout branch.</summary>
    Task CloneAndBranchAsync(
        string repoUrl,
        string workDirectory,
        string branchName,
        string fromRef,
        CancellationToken cancellationToken = default);

    Task CreateBranchAsync(string repoUrl, string branchName, string fromRef, CancellationToken cancellationToken = default);

    Task CommitAsync(CommitRequest request, CancellationToken cancellationToken = default);

    Task PushAsync(string workDirectory, string branchName, CancellationToken cancellationToken = default);

    Task<PullRequestResult> OpenPullRequestAsync(PullRequestRequest request, CancellationToken cancellationToken = default);
}
