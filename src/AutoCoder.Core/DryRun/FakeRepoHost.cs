using AutoCoder.Abstractions;

namespace AutoCoder.Core.DryRun;

public sealed class FakeRepoHost : IRepoHost
{
    private readonly HashSet<string> _allowlist;
    private readonly bool _allowAny;

    public FakeRepoHost(IEnumerable<string>? allowlist = null)
    {
        var list = (allowlist ?? []).Where(u => !string.IsNullOrWhiteSpace(u)).ToList();
        _allowAny = list.Count == 0;
        _allowlist = new HashSet<string>(list, StringComparer.OrdinalIgnoreCase);
    }

    public Task EnsureAllowlistedAsync(string repoUrl, CancellationToken cancellationToken = default)
    {
        if (_allowAny)
            return Task.CompletedTask;

        if (!_allowlist.Contains(repoUrl))
        {
            throw new InvalidOperationException(
                $"Repo '{repoUrl}' is not on the allowlist. Refusing to proceed.");
        }

        return Task.CompletedTask;
    }

    public Task CloneAndBranchAsync(
        string repoUrl,
        string workDirectory,
        string branchName,
        string fromRef,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(workDirectory);
        Console.WriteLine($"[dry-run] Would clone {repoUrl} → {workDirectory} and checkout '{branchName}' from '{fromRef}'");
        return Task.CompletedTask;
    }

    public Task CreateBranchAsync(string repoUrl, string branchName, string fromRef, CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"[dry-run] Would create branch '{branchName}' from '{fromRef}' on {repoUrl}");
        return Task.CompletedTask;
    }

    public Task CommitAsync(CommitRequest request, CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"[dry-run] Would commit on '{request.Branch}': {request.Message}");
        return Task.CompletedTask;
    }

    public Task PushAsync(string workDirectory, string branchName, CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"[dry-run] Would push '{branchName}' from {workDirectory}");
        return Task.CompletedTask;
    }

    public Task<PullRequestResult> OpenPullRequestAsync(PullRequestRequest request, CancellationToken cancellationToken = default)
    {
        var number = 42;
        var url = $"{request.RepoUrl.TrimEnd('/')}/pull/{number}";
        Console.WriteLine();
        Console.WriteLine("[dry-run] Fake PR payload:");
        Console.WriteLine($"  title:  {request.Title}");
        Console.WriteLine($"  base:   {request.BaseBranch}");
        Console.WriteLine($"  head:   {request.HeadBranch}");
        Console.WriteLine($"  draft:  {request.Draft}");
        Console.WriteLine($"  url:    {url}");
        Console.WriteLine("  body:");
        foreach (var line in request.Body.Split('\n'))
            Console.WriteLine($"    {line}");

        return Task.FromResult(new PullRequestResult
        {
            Url = url,
            Number = number,
            DryRun = true
        });
    }
}
