using AutoCoder.Abstractions;
using AutoCoder.Abstractions.Config;
using AutoCoder.Core;
using AutoCoder.Core.Config;
using AutoCoder.Core.Jira;
using AutoCoder.Core.Llm;
using AutoCoder.Core.Pipelines;
using AutoCoder.Core.Runs;

namespace AutoCoder.Core.Webhooks;

public sealed class WebhookTriggerDecision
{
    public bool ShouldRun { get; init; }
    public string Reason { get; init; } = "";
    public string? ProjectName { get; init; }
    public ProjectOptions? Project { get; init; }
}

public static class WebhookTriggerFilter
{
    public static bool IsWebhookTriggerMode(TriggersOptions triggers)
    {
        var mode = triggers.Mode?.Trim().ToLowerInvariant() ?? "cli";
        return mode is "webhook" or "both";
    }

    public static WebhookTriggerDecision Evaluate(AutoCoderOptions options, Ticket ticket)
    {
        ProjectCatalog.ApplyRuntimeOverlays(options);

        foreach (var (name, project) in options.Projects)
        {
            var trigger = project.JiraTrigger;
            if (trigger is null)
                continue;

            var tag = trigger.ProjectResolution?.Value;
            if (!string.IsNullOrWhiteSpace(tag)
                && !ticket.Labels.Any(l => string.Equals(l, tag, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (trigger.TriggerStatuses.Count > 0
                && !trigger.TriggerStatuses.Any(s =>
                    string.Equals(s, ticket.Status, StringComparison.OrdinalIgnoreCase)))
            {
                return new WebhookTriggerDecision
                {
                    ShouldRun = false,
                    Reason = $"Matched project '{name}' but status '{ticket.Status}' not in trigger_statuses.",
                    ProjectName = name,
                    Project = project
                };
            }

            return new WebhookTriggerDecision
            {
                ShouldRun = true,
                Reason = $"Matched project '{name}'.",
                ProjectName = name,
                Project = project
            };
        }

        if (options.Projects.Count == 1)
        {
            var only = options.Projects.First();
            return new WebhookTriggerDecision
            {
                ShouldRun = true,
                Reason = $"Using sole configured project '{only.Key}'.",
                ProjectName = only.Key,
                Project = only.Value
            };
        }

        return new WebhookTriggerDecision
        {
            ShouldRun = false,
            Reason = "No project matched ticket labels / jira_trigger."
        };
    }
}

public sealed class WebhookRunDispatcher
{
    private readonly AutoCoderOptions _options;

    public WebhookRunDispatcher(AutoCoderOptions options) => _options = options;

    /// <summary>
    /// Lease + Jira "running" ack, then pipeline on a background task.
    /// HTTP can return 202 without waiting for the PR.
    /// </summary>
    public bool TryEnqueue(Ticket ticket, ProjectOptions project, string projectName, out string runId, out string? skipReason)
    {
        runId = "";
        skipReason = null;
        var artifacts = ArtifactsDir();
        if (!TicketRunLease.TryAcquire(artifacts, ticket.Key, out skipReason))
            return false;

        runId = PipelineRunner.NewRunId(ticket.Key.ToLowerInvariant());
        var capturedRunId = runId;
        _ = Task.Run(async () =>
        {
            try
            {
                await RunAcceptedAsync(ticket, project, projectName, capturedRunId, acquireLease: false, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[webhook] background run {ticket.Key} failed: {ex.Message}");
            }
        });
        return true;
    }

    public Task<string> DispatchAsync(Ticket ticket, ProjectOptions project, string projectName, CancellationToken cancellationToken = default) =>
        RunAcceptedAsync(ticket, project, projectName, runId: null, acquireLease: true, cancellationToken);

    private async Task<string> RunAcceptedAsync(
        Ticket ticket,
        ProjectOptions project,
        string projectName,
        string? runId,
        bool acquireLease,
        CancellationToken cancellationToken)
    {
        var dryRun = _options.Webhooks.DryRun;
        var resolved = ProjectCatalog.Resolve(_options, ticket, projectName);
        var artifacts = ArtifactsDir();
        Directory.CreateDirectory(artifacts);

        if (acquireLease && !TicketRunLease.TryAcquire(artifacts, ticket.Key, out var skip))
        {
            Console.WriteLine($"[lease] Skip {ticket.Key}: {skip}");
            return $"skipped:{ticket.Key}";
        }

        runId ??= PipelineRunner.NewRunId(ticket.Key.ToLowerInvariant());
        await AcknowledgeRunningAsync(ticket, resolved, dryRun, cancellationToken);

        ITicketSource ticketSource = CompositeTicketSource.WithJiraWriteback(
            new InMemoryTicketSource(ticket),
            resolved.JiraBaseUrl,
            live: !dryRun);
        ILlmProvider llm = LlmProviderFactory.Create(_options, project.Agent);
        var (sandbox, repoHost, gate) = LiveAdapterFactory.Create(_options, dryRun, autoApprove: true);

        var pipeline = new FixBugPipeline(_options, ticketSource, llm, gate, sandbox, repoHost);
        var context = new PipelineContext
        {
            RunId = runId,
            PipelineName = pipeline.Name,
            DryRun = dryRun,
            ArtifactsDirectory = artifacts,
            ProjectName = resolved.ProjectName,
            RepoUrl = resolved.Repo.Url,
            BaseBranch = string.IsNullOrWhiteSpace(resolved.Repo.DefaultBranch) ? "main" : resolved.Repo.DefaultBranch,
            JiraBaseUrl = resolved.JiraBaseUrl,
            TicketBrowseUrl = ProjectCatalog.BrowseUrl(resolved.JiraBaseUrl, ticket.Key),
            Items =
            {
                ["ticketKey"] = "from-webhook",
                ["projectName"] = resolved.ProjectName
            }
        };

        await new PipelineRunner().RunAsync(pipeline, context, cancellationToken);
        return runId;
    }

    private async Task AcknowledgeRunningAsync(
        Ticket ticket,
        ResolvedProject resolved,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var running = string.IsNullOrWhiteSpace(resolved.Tracker.RunningStatus)
            ? "AgentWorking"
            : resolved.Tracker.RunningStatus;
        var comment = "AutoCoder accepted this ticket and is working on it. Jira will move to In Review (PR opened) or Agent Failure when the run finishes.";
        if (dryRun)
        {
            Console.WriteLine($"[jira] Dry-run ack {ticket.Key} → {running}");
            return;
        }

        try
        {
            var source = CompositeTicketSource.WithJiraWriteback(
                new InMemoryTicketSource(ticket),
                resolved.JiraBaseUrl,
                live: true);
            await source.WritebackAsync(new TicketWriteback
            {
                TicketKey = ticket.Key,
                NewStatus = running,
                Comment = comment
            }, cancellationToken);
            Console.WriteLine($"[jira] Accepted {ticket.Key} → {running}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[jira] Running ack failed for {ticket.Key}: {ex.Message}");
        }
    }

    private string ArtifactsDir() => Path.GetFullPath(_options.Webhooks.ArtifactsDirectory);
}
