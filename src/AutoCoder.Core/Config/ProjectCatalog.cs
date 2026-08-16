using AutoCoder.Abstractions;
using AutoCoder.Abstractions.Config;

namespace AutoCoder.Core.Config;

public sealed class ResolvedProject
{
    public required string ProjectName { get; init; }
    public required ProjectOptions Project { get; init; }
    public required string RepoKey { get; init; }
    public required RepoOptions Repo { get; init; }
    public required TrackerOptions Tracker { get; init; }
    public required string JiraBaseUrl { get; init; }
}

public static class ProjectCatalog
{
    public static string NormalizeBaseUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "";
        return url.Trim().TrimEnd('/');
    }

    public static string? BrowseUrl(string? jiraBaseUrl, string ticketKey)
    {
        var baseUrl = NormalizeBaseUrl(jiraBaseUrl);
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(ticketKey))
            return null;
        return $"{baseUrl}/browse/{ticketKey}";
    }

    /// <summary>Apply JIRA_BASE_URL / GITHUB_REPO_URL env overlays onto loaded options.</summary>
    public static void ApplyRuntimeOverlays(AutoCoderOptions options)
    {
        var jiraBase = Environment.GetEnvironmentVariable("JIRA_BASE_URL");
        if (!string.IsNullOrWhiteSpace(jiraBase))
        {
            foreach (var tracker in options.Trackers.Values.Where(t =>
                         t.Type.Equals("jira", StringComparison.OrdinalIgnoreCase)))
            {
                tracker.Url = jiraBase.Trim();
            }
        }

        var githubRepo = Environment.GetEnvironmentVariable("GITHUB_REPO_URL");
        if (!string.IsNullOrWhiteSpace(githubRepo))
        {
            foreach (var repo in options.Repos.Values.Where(r =>
                         r.Type.Equals("github", StringComparison.OrdinalIgnoreCase)))
            {
                if (string.IsNullOrWhiteSpace(repo.Url)
                    || repo.Url.Contains("your-org", StringComparison.OrdinalIgnoreCase)
                    || repo.Url.Contains("${", StringComparison.Ordinal))
                {
                    repo.Url = githubRepo.Trim();
                }
            }

            // If only one github repo entry exists, always allow env override.
            if (options.Repos.Count == 1)
            {
                var only = options.Repos.Values.First();
                only.Url = githubRepo.Trim();
            }
        }
    }

    public static ResolvedProject Resolve(AutoCoderOptions options, Ticket? ticket = null, string? projectName = null)
    {
        ApplyRuntimeOverlays(options);

        ProjectOptions project;
        string name;

        if (!string.IsNullOrWhiteSpace(projectName)
            && options.Projects.TryGetValue(projectName, out var named))
        {
            name = projectName;
            project = named;
        }
        else if (ticket is not null)
        {
            var match = MatchByJiraTag(options, ticket);
            if (match is null)
            {
                // Fall back to single configured project.
                if (options.Projects.Count != 1)
                {
                    throw new InvalidOperationException(
                        "Could not resolve project from ticket labels. Set projects.*.jira_trigger.project_resolution.value "
                        + "to a label on the ticket, or pass --project, or keep exactly one project in config.");
                }

                var only = options.Projects.First();
                name = only.Key;
                project = only.Value;
            }
            else
            {
                name = match.Value.Name;
                project = match.Value.Project;
            }
        }
        else if (options.Projects.Count == 1)
        {
            var only = options.Projects.First();
            name = only.Key;
            project = only.Value;
        }
        else
        {
            throw new InvalidOperationException("No project resolved. Pass --project or configure a single project.");
        }

        if (string.IsNullOrWhiteSpace(project.Tracker)
            || !options.Trackers.TryGetValue(project.Tracker, out var tracker))
        {
            throw new InvalidOperationException(
                $"Project '{name}' tracker '{project.Tracker}' is missing from trackers: catalog.");
        }

        var repoKey = project.Repos.FirstOrDefault()
            ?? throw new InvalidOperationException($"Project '{name}' has no repos configured.");

        if (!options.Repos.TryGetValue(repoKey, out var repo))
            throw new InvalidOperationException($"Repo catalog entry '{repoKey}' not found.");

        var jiraBase = NormalizeBaseUrl(tracker.Url);
        if (string.IsNullOrWhiteSpace(jiraBase)
            || jiraBase.Contains("your-org", StringComparison.OrdinalIgnoreCase)
            || jiraBase.Contains("${", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Jira base URL is not configured. Set trackers.<name>.url in autocoder.yml "
                + "or environment variable JIRA_BASE_URL (e.g. https://acme.atlassian.net).");
        }

        if (string.IsNullOrWhiteSpace(repo.Url)
            || repo.Url.Contains("your-org", StringComparison.OrdinalIgnoreCase)
            || repo.Url.Contains("${", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "GitHub repo URL is not configured. Set repos.<name>.url in autocoder.yml "
                + "or environment variable GITHUB_REPO_URL (e.g. https://github.com/org/repo).");
        }

        return new ResolvedProject
        {
            ProjectName = name,
            Project = project,
            RepoKey = repoKey,
            Repo = repo,
            Tracker = tracker,
            JiraBaseUrl = jiraBase
        };
    }

    private static (string Name, ProjectOptions Project)? MatchByJiraTag(AutoCoderOptions options, Ticket ticket)
    {
        foreach (var (name, project) in options.Projects)
        {
            var tag = project.JiraTrigger?.ProjectResolution?.Value;
            if (string.IsNullOrWhiteSpace(tag))
                continue;
            if (ticket.Labels.Any(l => string.Equals(l, tag, StringComparison.OrdinalIgnoreCase)))
                return (name, project);
        }

        return null;
    }
}
