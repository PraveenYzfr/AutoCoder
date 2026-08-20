using AutoCoder.Abstractions;
using AutoCoder.Abstractions.Config;
using AutoCoder.Core.Webhooks;

namespace AutoCoder.Tests;

public sealed class JiraWebhookAndTriggerTests
{
    [Fact]
    public void Parses_sample_jira_payload()
    {
        var json = File.ReadAllText(FindSample("jira-webhook.json"));
        Assert.True(JiraWebhookParser.TryParse(json, out var parsed, out var error));
        Assert.Null(error);
        Assert.Equal("AC-101", parsed!.Ticket.Key);
        Assert.Equal("AssignedToAgent", parsed.Ticket.Status);
        Assert.Contains("autocoder", parsed.Ticket.Labels);
        Assert.Equal("Bug", parsed.Ticket.IssueType);
    }

    [Fact]
    public void Rejects_payload_without_issue()
    {
        Assert.False(JiraWebhookParser.TryParse("{\"webhookEvent\":\"x\"}", out _, out var error));
        Assert.Contains("issue", error);
    }

    [Fact]
    public void Trigger_status_only_runs_AssignedToAgent()
    {
        var options = OneProject(statusOnly: true);
        var go = WebhookTriggerFilter.Evaluate(options, TestContext.Ticket("AssignedToAgent"));
        Assert.True(go.ShouldRun);
        var skip = WebhookTriggerFilter.Evaluate(options, TestContext.Ticket("In Review"));
        Assert.False(skip.ShouldRun);
    }

    [Fact]
    public void Trigger_tag_requires_label()
    {
        var options = OneProject(statusOnly: false);
        options.Projects["other"] = new ProjectOptions
        {
            Agent = "default",
            Tracker = "jira",
            Repos = ["simpleapp"]
        };
        var miss = WebhookTriggerFilter.Evaluate(options, TestContext.Ticket("AssignedToAgent"));
        Assert.False(miss.ShouldRun);
        var hit = WebhookTriggerFilter.Evaluate(options, TestContext.Ticket("AssignedToAgent", "autocoder"));
        Assert.True(hit.ShouldRun);
    }

    [Theory]
    [InlineData("webhook", true)]
    [InlineData("both", true)]
    [InlineData("cli", false)]
    public void Trigger_mode(string mode, bool expected)
    {
        Assert.Equal(expected, WebhookTriggerFilter.IsWebhookTriggerMode(new TriggersOptions { Mode = mode }));
    }

    private static AutoCoderOptions OneProject(bool statusOnly)
    {
        var options = new AutoCoderOptions();
        options.Repos["simpleapp"] = new RepoOptions { Url = "https://github.com/PraveenYzfr/SimpleApp" };
        options.Trackers["jira"] = new TrackerOptions { Url = "https://example.atlassian.net" };
        options.Projects["simpleapp"] = new ProjectOptions
        {
            Agent = "default",
            Tracker = "jira",
            Repos = ["simpleapp"],
            JiraTrigger = new JiraTriggerOptions
            {
                TriggerStatuses = ["AssignedToAgent"],
                ProjectResolution = new ProjectResolutionOptions
                {
                    Strategy = statusOnly ? "status_only" : "tag",
                    Value = statusOnly ? "" : "autocoder"
                }
            }
        };
        return options;
    }

    private static string FindSample(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "samples", name);
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException(name);
    }
}
