using AutoCoder.Abstractions.Config;
using AutoCoder.Core.Config;
using AutoCoder.Core.Jira;
using AutoCoder.Core.Webhooks;

namespace AutoCoder.Server;

/// <summary>
/// If the VM was off when Jira fired the webhook, pick up tickets still in AssignedToAgent.
/// </summary>
public sealed class JiraPoller : BackgroundService
{
    private readonly AutoCoderOptions _options;
    private readonly WebhookRunDispatcher _dispatcher;

    public JiraPoller(AutoCoderOptions options, WebhookRunDispatcher dispatcher)
    {
        _options = options;
        _dispatcher = dispatcher;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Poll.Enabled)
        {
            Console.WriteLine("[poll] Disabled.");
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(60, _options.Poll.IntervalSeconds));
        Console.WriteLine($"[poll] Jira JQL every {interval.TotalSeconds:F0}s: {_options.Poll.Jql}");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"[poll] {ex.Message}");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        ProjectCatalog.ApplyRuntimeOverlays(_options);
        var baseUrl = _options.Trackers.Values
            .Select(t => ProjectCatalog.NormalizeBaseUrl(t.Url))
            .FirstOrDefault(u => !string.IsNullOrWhiteSpace(u));
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            Console.WriteLine("[poll] No Jira base URL.");
            return;
        }

        using var jira = new JiraTicketSource(baseUrl);
        var tickets = await jira.SearchJqlAsync(_options.Poll.Jql, cancellationToken);
        Console.WriteLine($"[poll] {tickets.Count} issue(s) for JQL.");

        foreach (var ticket in tickets)
        {
            var decision = WebhookTriggerFilter.Evaluate(_options, ticket);
            if (!decision.ShouldRun || decision.Project is null || decision.ProjectName is null)
            {
                Console.WriteLine($"[poll] Skip {ticket.Key}: {decision.Reason}");
                continue;
            }

            Console.WriteLine($"[poll] Dispatch {ticket.Key}");
            await _dispatcher.DispatchAsync(ticket, decision.Project, decision.ProjectName, cancellationToken);
        }
    }
}
