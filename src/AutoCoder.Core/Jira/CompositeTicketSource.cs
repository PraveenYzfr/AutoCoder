using AutoCoder.Abstractions;
using AutoCoder.Abstractions.Config;
using AutoCoder.Core.Config;

namespace AutoCoder.Core.Jira;

/// <summary>Fetches from one source (file/webhook) and writes comments/status to Jira.</summary>
public sealed class CompositeTicketSource : ITicketSource
{
    private readonly ITicketSource _fetch;
    private readonly ITicketSource _writeback;

    public CompositeTicketSource(ITicketSource fetch, ITicketSource writeback)
    {
        _fetch = fetch;
        _writeback = writeback;
    }

    public Task<Ticket> FetchAsync(string ticketKey, CancellationToken cancellationToken = default) =>
        _fetch.FetchAsync(ticketKey, cancellationToken);

    public Task WritebackAsync(TicketWriteback writeback, CancellationToken cancellationToken = default) =>
        _writeback.WritebackAsync(writeback, cancellationToken);

    public static ITicketSource WithJiraWriteback(ITicketSource inner, string? jiraBaseUrl, bool live)
    {
        if (!live)
            return inner;

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("JIRA_TOKEN")))
        {
            Console.WriteLine("[jira] JIRA_TOKEN missing — PR can still open, Jira will not be updated.");
            return inner;
        }

        var url = ProjectCatalog.NormalizeBaseUrl(jiraBaseUrl);
        if (string.IsNullOrWhiteSpace(url)
            || url.Contains("example.atlassian", StringComparison.OrdinalIgnoreCase)
            || url.Contains("${"))
        {
            Console.WriteLine("[jira] No real Jira base URL — skipping writeback.");
            return inner;
        }

        return new CompositeTicketSource(inner, new JiraTicketSource(url));
    }

    public static string? FirstJiraUrl(AutoCoderOptions options) =>
        options.Trackers.Values
            .Select(t => ProjectCatalog.NormalizeBaseUrl(t.Url))
            .FirstOrDefault(u => !string.IsNullOrWhiteSpace(u));
}
