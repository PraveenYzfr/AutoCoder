using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AutoCoder.Abstractions;
using AutoCoder.Core.Resilience;

namespace AutoCoder.Core.GitHub;

/// <summary>
/// GitHub operations. Auth is injected via <see cref="IGitCredentialProvider"/> —
/// PAT today, GitHub App tomorrow; pipeline code does not change.
/// </summary>
public sealed class GitHubRepoHost : IRepoHost, IDisposable
{
    private readonly HashSet<string> _allowlist;
    private readonly IGitCredentialProvider _credentials;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public GitHubRepoHost(
        IEnumerable<string> allowlist,
        IGitCredentialProvider credentials,
        HttpClient? httpClient = null)
    {
        _allowlist = new HashSet<string>(
            allowlist.Where(u => !string.IsNullOrWhiteSpace(u)),
            StringComparer.OrdinalIgnoreCase);
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _ownsHttp = httpClient is null;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AutoCoder");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        Console.WriteLine($"[github] Auth mode: {_credentials.Mode}");
    }

    public Task EnsureAllowlistedAsync(string repoUrl, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeRepoUrl(repoUrl);
        var ok = _allowlist.Count == 0
            || _allowlist.Any(a => NormalizeRepoUrl(a).Equals(normalized, StringComparison.OrdinalIgnoreCase)
                                   || normalized.Contains(a.Replace("https://", "", StringComparison.OrdinalIgnoreCase),
                                       StringComparison.OrdinalIgnoreCase));
        if (!ok)
            throw new InvalidOperationException($"Repo '{repoUrl}' is not on the allowlist.");
        return Task.CompletedTask;
    }

    public async Task CloneAndBranchAsync(
        string repoUrl,
        string workDirectory,
        string branchName,
        string fromRef,
        CancellationToken cancellationToken = default)
    {
        await EnsureAllowlistedAsync(repoUrl, cancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(workDirectory)!);

        var creds = await _credentials.GetCredentialsAsync(cancellationToken);
        var authUrl = ToAuthenticatedCloneUrl(repoUrl, creds.AccessToken);

        if (Directory.Exists(Path.Combine(workDirectory, ".git")))
        {
            await GitAsync(workDirectory, ["remote", "set-url", "origin", authUrl], cancellationToken);
            await GitAsync(workDirectory, ["fetch", "origin"], cancellationToken);
            await GitAsync(workDirectory, ["checkout", fromRef], cancellationToken);
            await GitAsync(workDirectory, ["pull", "--ff-only", "origin", fromRef], cancellationToken);
        }
        else
        {
            if (Directory.Exists(workDirectory))
                Directory.Delete(workDirectory, recursive: true);

            await GitAsync(Path.GetDirectoryName(workDirectory)!,
                ["clone", "--depth", "1", "--branch", fromRef, authUrl, workDirectory],
                cancellationToken);
        }

        await GitAsync(workDirectory, ["checkout", "-B", branchName], cancellationToken);
        // Keep origin authenticated for later push (token may refresh again in PushAsync).
        await GitAsync(workDirectory, ["remote", "set-url", "origin", authUrl], cancellationToken);
        Console.WriteLine($"[github] Workspace {workDirectory} on branch {branchName} (from {fromRef})");
    }

    public Task CreateBranchAsync(string repoUrl, string branchName, string fromRef, CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"[github] CreateBranch '{branchName}' from '{fromRef}' on {repoUrl}");
        return Task.CompletedTask;
    }

    public async Task CommitAsync(CommitRequest request, CancellationToken cancellationToken = default)
    {
        var work = request.WorkDirectory
            ?? throw new InvalidOperationException("CommitAsync requires WorkDirectory for live GitHub host.");

        await GitAsync(work, ["add", "-A"], cancellationToken);
        var status = await GitAsync(work, ["status", "--porcelain"], cancellationToken);
        if (string.IsNullOrWhiteSpace(status.StdOut))
        {
            Console.WriteLine("[github] Nothing to commit.");
            return;
        }

        await GitAsync(work,
            ["-c", "user.name=AutoCoder", "-c", "user.email=autocoder@local", "commit", "-m", request.Message],
            cancellationToken);
        Console.WriteLine($"[github] Committed: {request.Message}");
    }

    public async Task PushAsync(string workDirectory, string branchName, CancellationToken cancellationToken = default)
    {
        // Refresh token (important for GitHub App short-lived tokens).
        var creds = await _credentials.GetCredentialsAsync(cancellationToken);
        var origin = await GitAsync(workDirectory, ["remote", "get-url", "origin"], cancellationToken);
        var repoUrl = StripAuthFromRemote(origin.StdOut.Trim());
        var authUrl = ToAuthenticatedCloneUrl(repoUrl, creds.AccessToken);
        await GitAsync(workDirectory, ["remote", "set-url", "origin", authUrl], cancellationToken);
        await GitAsync(workDirectory, ["push", "-u", "origin", branchName, "--force-with-lease"], cancellationToken);
        Console.WriteLine($"[github] Pushed branch {branchName}");
    }

    public async Task<PullRequestResult> OpenPullRequestAsync(PullRequestRequest request, CancellationToken cancellationToken = default)
    {
        var (owner, repo) = ParseOwnerRepo(request.RepoUrl);
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["title"] = request.Title,
            ["head"] = request.HeadBranch,
            ["base"] = request.BaseBranch,
            ["body"] = request.Body,
            ["draft"] = request.Draft
        });

        using var response = await TransientRetry.SendAsync("github.pr", async ct =>
        {
            var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"https://api.github.com/repos/{owner}/{repo}/pulls")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            await ApplyAuthAsync(httpRequest, ct);
            return await _http.SendAsync(httpRequest, ct);
        }, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            if ((int)response.StatusCode is 422 && raw.Contains("A pull request already exists", StringComparison.OrdinalIgnoreCase))
            {
                var existing = await FindExistingPrAsync(owner, repo, request.HeadBranch, request.BaseBranch, cancellationToken);
                if (existing is not null)
                    return existing;
            }

            throw new InvalidOperationException($"GitHub open PR failed {(int)response.StatusCode}: {Truncate(raw, 600)}");
        }

        using var doc = JsonDocument.Parse(raw);
        var htmlUrl = doc.RootElement.GetProperty("html_url").GetString()
            ?? throw new InvalidOperationException("PR response missing html_url.");
        var number = doc.RootElement.TryGetProperty("number", out var n) ? n.GetInt32() : (int?)null;

        Console.WriteLine($"[github] Opened PR {htmlUrl}");
        return new PullRequestResult { Url = htmlUrl, Number = number, DryRun = false };
    }

    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }

    private async Task ApplyAuthAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var creds = await _credentials.GetCredentialsAsync(cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue(creds.AuthorizationScheme, creds.AccessToken);
    }

    private async Task<PullRequestResult?> FindExistingPrAsync(
        string owner, string repo, string head, string @base, CancellationToken ct)
    {
        var url = $"https://api.github.com/repos/{owner}/{repo}/pulls?state=open&head={owner}:{Uri.EscapeDataString(head)}&base={Uri.EscapeDataString(@base)}";
        using var response = await TransientRetry.SendAsync("github.pr.lookup", async token =>
        {
            var httpRequest = new HttpRequestMessage(HttpMethod.Get, url);
            await ApplyAuthAsync(httpRequest, token);
            return await _http.SendAsync(httpRequest, token);
        }, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            return null;

        using var doc = JsonDocument.Parse(raw);
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            return null;

        var first = doc.RootElement[0];
        return new PullRequestResult
        {
            Url = first.GetProperty("html_url").GetString() ?? "",
            Number = first.TryGetProperty("number", out var n) ? n.GetInt32() : null,
            DryRun = false
        };
    }

    private static string ToAuthenticatedCloneUrl(string repoUrl, string token)
    {
        var (owner, repo) = ParseOwnerRepo(repoUrl);
        return $"https://x-access-token:{token}@github.com/{owner}/{repo}.git";
    }

    private static string StripAuthFromRemote(string remoteUrl)
    {
        // https://x-access-token:TOKEN@github.com/org/repo.git → https://github.com/org/repo
        var m = Regex.Match(remoteUrl, @"github\.com[/:](?<o>[^/]+)/(?<r>[^/]+?)(?:\.git)?$", RegexOptions.IgnoreCase);
        if (m.Success)
            return $"https://github.com/{m.Groups["o"].Value}/{m.Groups["r"].Value}";
        return NormalizeRepoUrl(remoteUrl);
    }

    public static (string Owner, string Repo) ParseOwnerRepo(string repoUrl)
    {
        var normalized = NormalizeRepoUrl(repoUrl);
        var match = Regex.Match(normalized, @"^https://github\.com/(?<o>[^/]+)/(?<r>[^/]+)$", RegexOptions.IgnoreCase);
        if (!match.Success)
            throw new InvalidOperationException($"Cannot parse GitHub owner/repo from '{repoUrl}'.");
        return (match.Groups["o"].Value, match.Groups["r"].Value);
    }

    public static string NormalizeRepoUrl(string repoUrl)
    {
        var u = repoUrl.Trim().TrimEnd('/');
        if (u.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            u = u[..^4];
        if (u.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase))
            u = "https://github.com/" + u["git@github.com:".Length..];
        // Strip embedded credentials if present
        u = Regex.Replace(u, @"https://[^@]+@", "https://");
        return u;
    }

    private static Task<SandboxCommandResult> GitAsync(
        string workDirectory,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var op = args.Count > 0 ? $"git.{args[0]}" : "git";
        if (!ShouldRetryGit(args))
            return GitOnceAsync(workDirectory, args, cancellationToken);

        return TransientRetry.RunAsync(op, async ct =>
        {
            try
            {
                return await GitOnceAsync(workDirectory, args, ct);
            }
            catch (InvalidOperationException ex) when (TransientRetry.IsTransientGit(ex.Message))
            {
                throw new TransientFailureException(op, ex.Message, inner: ex);
            }
        }, cancellationToken);
    }

    private static bool ShouldRetryGit(IReadOnlyList<string> args) =>
        args.Count > 0 && args[0] is "clone" or "fetch" or "pull" or "push";

    private static async Task<SandboxCommandResult> GitOnceAsync(
        string workDirectory,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        if (args.Count > 0 && args[0] == "clone")
        {
            var dest = args[^1];
            if (Directory.Exists(dest))
            {
                try { Directory.Delete(dest, recursive: true); }
                catch { /* clone will fail with a clear error if dest cannot be removed */ }
            }
        }

        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        var logArgs = args.Select(a =>
            a.Contains("x-access-token:", StringComparison.OrdinalIgnoreCase) ? "[redacted-url]" : a);
        Console.WriteLine($"[git] {string.Join(' ', logArgs)}");

        using var process = new Process { StartInfo = psi };
        var stdOut = new StringBuilder();
        var stdErr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdOut.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stdErr.AppendLine(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {args[0]} failed ({process.ExitCode}): {stdErr}");
        }

        return new SandboxCommandResult
        {
            ExitCode = process.ExitCode,
            StdOut = stdOut.ToString(),
            StdErr = stdErr.ToString()
        };
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";
}
