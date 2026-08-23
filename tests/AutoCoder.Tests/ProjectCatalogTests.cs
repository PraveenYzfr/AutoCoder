using AutoCoder.Abstractions.Config;
using AutoCoder.Core.Config;

namespace AutoCoder.Tests;

public sealed class ProjectCatalogTests : IDisposable
{
    private readonly string? _prevJira = Environment.GetEnvironmentVariable("JIRA_BASE_URL");
    private readonly string? _prevRepo = Environment.GetEnvironmentVariable("GITHUB_REPO_URL");

    [Fact]
    public void BrowseUrl_builds_browse_link_and_trims_trailing_slash()
    {
        Assert.Equal(
            "https://acme.atlassian.net/browse/AC-1",
            ProjectCatalog.BrowseUrl("https://acme.atlassian.net/", "AC-1"));
    }

    [Fact]
    public void BrowseUrl_is_null_without_a_base_url_or_ticket_key()
    {
        Assert.Null(ProjectCatalog.BrowseUrl(null, "AC-1"));
        Assert.Null(ProjectCatalog.BrowseUrl("https://acme.atlassian.net", ""));
    }

    [Fact]
    public void Resolve_matches_project_by_ticket_label_over_the_single_project_fallback()
    {
        var options = TwoProjects();
        var ticket = TestContext.Ticket("AssignedToAgent", "other-tag");
        var resolved = ProjectCatalog.Resolve(options, ticket);
        Assert.Equal("other", resolved.ProjectName);
    }

    [Fact]
    public void Resolve_falls_back_to_the_sole_project_when_no_label_matches()
    {
        var options = OneProject();
        var ticket = TestContext.Ticket("AssignedToAgent", "unrelated-label");
        var resolved = ProjectCatalog.Resolve(options, ticket);
        Assert.Equal("simpleapp", resolved.ProjectName);
    }

    [Fact]
    public void Resolve_throws_when_multiple_projects_and_no_label_matches()
    {
        var options = TwoProjects();
        var ticket = TestContext.Ticket("AssignedToAgent", "no-such-label");
        var ex = Assert.Throws<InvalidOperationException>(() => ProjectCatalog.Resolve(options, ticket));
        Assert.Contains("Could not resolve project", ex.Message);
    }

    [Fact]
    public void Resolve_prefers_explicit_project_name_over_ticket_labels()
    {
        var options = TwoProjects();
        var ticket = TestContext.Ticket("AssignedToAgent", "other-tag");
        var resolved = ProjectCatalog.Resolve(options, ticket, projectName: "simpleapp");
        Assert.Equal("simpleapp", resolved.ProjectName);
    }

    [Fact]
    public void Resolve_throws_on_placeholder_jira_base_url()
    {
        var options = OneProject();
        options.Trackers["jira"].Url = "https://your-org.atlassian.net";
        var ex = Assert.Throws<InvalidOperationException>(() => ProjectCatalog.Resolve(options));
        Assert.Contains("Jira base URL is not configured", ex.Message);
    }

    [Fact]
    public void Resolve_throws_on_placeholder_repo_url()
    {
        var options = OneProject();
        options.Repos["simpleapp"].Url = "https://github.com/your-org/${repo}";
        var ex = Assert.Throws<InvalidOperationException>(() => ProjectCatalog.Resolve(options));
        Assert.Contains("GitHub repo URL is not configured", ex.Message);
    }

    [Fact]
    public void Resolve_throws_when_tracker_is_missing_from_catalog()
    {
        var options = OneProject();
        options.Projects["simpleapp"].Tracker = "does-not-exist";
        var ex = Assert.Throws<InvalidOperationException>(() => ProjectCatalog.Resolve(options));
        Assert.Contains("tracker 'does-not-exist' is missing", ex.Message);
    }

    [Fact]
    public void ApplyRuntimeOverlays_sets_jira_url_on_jira_trackers_only()
    {
        Environment.SetEnvironmentVariable("JIRA_BASE_URL", "https://from-env.atlassian.net");
        var options = OneProject();
        options.Trackers["not-jira"] = new TrackerOptions { Type = "github-issues", Url = "https://untouched.example" };
        ProjectCatalog.ApplyRuntimeOverlays(options);
        Assert.Equal("https://from-env.atlassian.net", options.Trackers["jira"].Url);
        Assert.Equal("https://untouched.example", options.Trackers["not-jira"].Url);
    }

    [Fact]
    public void ApplyRuntimeOverlays_overrides_the_sole_repo_even_without_a_placeholder()
    {
        Environment.SetEnvironmentVariable("GITHUB_REPO_URL", "https://github.com/PraveenYzfr/SimpleApp");
        var options = OneProject();
        options.Repos["simpleapp"].Url = "https://github.com/SomeoneElse/OtherRepo";
        ProjectCatalog.ApplyRuntimeOverlays(options);
        Assert.Equal("https://github.com/PraveenYzfr/SimpleApp", options.Repos["simpleapp"].Url);
    }

    [Fact]
    public void ApplyRuntimeOverlays_only_replaces_placeholder_urls_when_multiple_repos_exist()
    {
        Environment.SetEnvironmentVariable("GITHUB_REPO_URL", "https://github.com/PraveenYzfr/SimpleApp");
        var options = TwoProjects();
        options.Repos["simpleapp"].Url = "https://github.com/your-org/simpleapp";
        options.Repos["other-repo"].Url = "https://github.com/AlreadySet/DoNotTouch";
        ProjectCatalog.ApplyRuntimeOverlays(options);
        Assert.Equal("https://github.com/PraveenYzfr/SimpleApp", options.Repos["simpleapp"].Url);
        Assert.Equal("https://github.com/AlreadySet/DoNotTouch", options.Repos["other-repo"].Url);
    }

    private static AutoCoderOptions OneProject()
    {
        var options = new AutoCoderOptions();
        options.Repos["simpleapp"] = new RepoOptions { Url = "https://github.com/PraveenYzfr/SimpleApp" };
        options.Trackers["jira"] = new TrackerOptions { Url = "https://acme.atlassian.net" };
        options.Projects["simpleapp"] = new ProjectOptions
        {
            Agent = "default",
            Tracker = "jira",
            Repos = ["simpleapp"]
        };
        return options;
    }

    private static AutoCoderOptions TwoProjects()
    {
        var options = OneProject();
        options.Repos["other-repo"] = new RepoOptions { Url = "https://github.com/PraveenYzfr/OtherRepo" };
        options.Projects["other"] = new ProjectOptions
        {
            Agent = "default",
            Tracker = "jira",
            Repos = ["other-repo"],
            JiraTrigger = new JiraTriggerOptions
            {
                ProjectResolution = new ProjectResolutionOptions { Strategy = "tag", Value = "other-tag" }
            }
        };
        return options;
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("JIRA_BASE_URL", _prevJira);
        Environment.SetEnvironmentVariable("GITHUB_REPO_URL", _prevRepo);
    }
}
