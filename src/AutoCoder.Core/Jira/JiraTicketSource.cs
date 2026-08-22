using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AutoCoder.Abstractions;
using AutoCoder.Core.Config;
using AutoCoder.Core.Resilience;

namespace AutoCoder.Core.Jira;

/// <summary>
/// Fetches a Jira issue by key using the configured base URL.
/// Auth: JIRA_EMAIL + JIRA_TOKEN (Basic) or JIRA_TOKEN alone as Bearer.
/// </summary>
public sealed class JiraTicketSource : ITicketSource, IDisposable
{
    private readonly string _baseUrl;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public JiraTicketSource(string jiraBaseUrl, HttpClient? httpClient = null)
    {
        _baseUrl = ProjectCatalog.NormalizeBaseUrl(jiraBaseUrl);
        if (string.IsNullOrWhiteSpace(_baseUrl))
            throw new ArgumentException("Jira base URL is required.", nameof(jiraBaseUrl));

        _ownsHttp = httpClient is null;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        ApplyAuth(_http);
    }

    public async Task<Ticket> FetchAsync(string ticketKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ticketKey)
            || ticketKey is "from-file" or "from-webhook")
        {
            throw new InvalidOperationException("A real Jira ticket key is required (e.g. AC-101).");
        }

        var url = $"{_baseUrl}/rest/api/3/issue/{Uri.EscapeDataString(ticketKey)}";
        var raw = await SendAsync($"jira.fetch.{ticketKey}", ct => _http.GetAsync(url, ct), cancellationToken);

        using var doc = JsonDocument.Parse(raw);
        return ParseIssue(doc.RootElement);
    }

    public async Task<IReadOnlyList<Ticket>> SearchJqlAsync(string jql, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Serialize(new
        {
            jql,
            maxResults = 25,
            fields = new[] { "summary", "description", "status", "labels", "assignee", "project", "issuetype", "priority" }
        });
        var url = $"{_baseUrl}/rest/api/3/search/jql";
        string raw;
        using (var post = await TransientRetry.SendAsync(
                   "jira.search",
                   ct => _http.PostAsync(url, new StringContent(payload, Encoding.UTF8, "application/json"), ct),
                   cancellationToken))
        {
            raw = await post.Content.ReadAsStringAsync(cancellationToken);
            if (!post.IsSuccessStatusCode)
            {
                var fallback = $"{_baseUrl}/rest/api/3/search?jql={Uri.EscapeDataString(jql)}&maxResults=25&fields=summary,description,status,labels,assignee,project,issuetype,priority";
                raw = await SendAsync("jira.search.legacy", ct => _http.GetAsync(fallback, ct), cancellationToken);
            }
        }

        using var doc = JsonDocument.Parse(raw);
        var tickets = new List<Ticket>();
        if (!doc.RootElement.TryGetProperty("issues", out var issues) || issues.ValueKind != JsonValueKind.Array)
            return tickets;

        foreach (var issue in issues.EnumerateArray())
            tickets.Add(ParseIssue(issue));
        return tickets;
    }

    public static Ticket ParseIssue(JsonElement issue)
    {
        var key = issue.TryGetProperty("key", out var keyEl) ? keyEl.GetString() ?? "" : "";
        var fields = issue.TryGetProperty("fields", out var f) ? f : issue;

        var summary = fields.TryGetProperty("summary", out var s) ? s.GetString() ?? key : key;
        var description = ExtractDescription(fields);
        var status = fields.TryGetProperty("status", out var st) && st.TryGetProperty("name", out var sn)
            ? sn.GetString() ?? ""
            : "";

        var labels = new List<string>();
        if (fields.TryGetProperty("labels", out var labelsEl) && labelsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var label in labelsEl.EnumerateArray())
            {
                var v = label.GetString();
                if (!string.IsNullOrWhiteSpace(v))
                    labels.Add(v);
            }
        }

        string? assignee = null;
        if (fields.TryGetProperty("assignee", out var a) && a.ValueKind == JsonValueKind.Object)
            assignee = a.TryGetProperty("displayName", out var dn) ? dn.GetString() : null;

        var projectKey = fields.TryGetProperty("project", out var p) && p.TryGetProperty("key", out var pk)
            ? pk.GetString()
            : null;
        var issueType = fields.TryGetProperty("issuetype", out var it) && it.TryGetProperty("name", out var itn)
            ? itn.GetString()
            : null;
        var priority = fields.TryGetProperty("priority", out var pr) && pr.ValueKind == JsonValueKind.Object
            && pr.TryGetProperty("name", out var prn)
            ? prn.GetString()
            : null;

        return new Ticket
        {
            Key = key,
            Summary = summary,
            Description = description,
            Status = status,
            Labels = labels,
            Assignee = assignee,
            ProjectKey = projectKey,
            IssueType = issueType,
            Priority = priority,
            Comments = []
        };
    }

    public async Task WritebackAsync(TicketWriteback writeback, CancellationToken cancellationToken = default)
    {
        Console.WriteLine();
        Console.WriteLine("[jira] Writeback:");
        Console.WriteLine($"  browse:  {ProjectCatalog.BrowseUrl(_baseUrl, writeback.TicketKey)}");
        Console.WriteLine($"  status:  {writeback.NewStatus ?? "(unchanged)"}");
        Console.WriteLine($"  comment: {writeback.Comment}");

        if (!string.IsNullOrWhiteSpace(writeback.Comment))
            await PostCommentAsync(writeback.TicketKey, writeback.Comment, cancellationToken);

        if (!string.IsNullOrWhiteSpace(writeback.NewStatus))
            await TransitionAsync(writeback.TicketKey, writeback.NewStatus, cancellationToken);
    }

    private async Task PostCommentAsync(string key, string comment, CancellationToken cancellationToken)
    {
        var paragraphs = comment
            .Replace("\r\n", "\n")
            .Split('\n')
            .Take(40)
            .Select(line => (object)new
            {
                type = "paragraph",
                content = string.IsNullOrEmpty(line)
                    ? Array.Empty<object>()
                    : new object[] { new { type = "text", text = line.Length > 4000 ? line[..4000] : line } }
            })
            .ToArray();

        var payload = new
        {
            body = new
            {
                type = "doc",
                version = 1,
                content = paragraphs
            }
        };

        var json = JsonSerializer.Serialize(payload);
        var url = $"{_baseUrl}/rest/api/3/issue/{Uri.EscapeDataString(key)}/comment";
        await SendAsync(
            $"jira.comment.{key}",
            ct => _http.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"), ct),
            cancellationToken);
        Console.WriteLine("[jira] Comment posted.");
    }

    private async Task TransitionAsync(string key, string statusName, CancellationToken cancellationToken)
    {
        var url = $"{_baseUrl}/rest/api/3/issue/{Uri.EscapeDataString(key)}/transitions";
        var raw = await SendAsync($"jira.transitions.{key}", ct => _http.GetAsync(url, ct), cancellationToken);

        using var doc = JsonDocument.Parse(raw);
        string? id = null;
        if (doc.RootElement.TryGetProperty("transitions", out var list))
        {
            foreach (var t in list.EnumerateArray())
            {
                var name = t.TryGetProperty("name", out var n) ? n.GetString() : "";
                var to = t.TryGetProperty("to", out var toEl) && toEl.TryGetProperty("name", out var tn)
                    ? tn.GetString()
                    : "";
                if (string.Equals(name, statusName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(to, statusName, StringComparison.OrdinalIgnoreCase))
                {
                    id = t.GetProperty("id").GetString();
                    break;
                }
            }
        }

        if (id is null)
        {
            Console.WriteLine($"[jira] No transition to '{statusName}' — comment posted, status left unchanged.");
            return;
        }

        var body = JsonSerializer.Serialize(new { transition = new { id } });
        await SendAsync(
            $"jira.transition.{key}",
            ct => _http.PostAsync(url, new StringContent(body, Encoding.UTF8, "application/json"), ct),
            cancellationToken);
        Console.WriteLine($"[jira] Transitioned to {statusName}.");
    }

    private async Task<string> SendAsync(
        string operation,
        Func<CancellationToken, Task<HttpResponseMessage>> send,
        CancellationToken cancellationToken)
    {
        using var response = await TransientRetry.SendAsync(operation, send, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"{operation} failed {(int)response.StatusCode}: {Truncate(raw, 400)}");
        }

        return raw;
    }

    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }

    private static void ApplyAuth(HttpClient http)
    {
        var token = Environment.GetEnvironmentVariable("JIRA_TOKEN");
        var email = Environment.GetEnvironmentVariable("JIRA_EMAIL");

        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException(
                "JIRA_TOKEN is not set. For Jira Cloud use an API token with JIRA_EMAIL "
                + "(Basic auth), or set a bearer-capable token.");
        }

        if (!string.IsNullOrWhiteSpace(email))
        {
            var raw = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{token}"));
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", raw);
        }
        else
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    private static string ExtractDescription(JsonElement fields)
    {
        if (!fields.TryGetProperty("description", out var desc) || desc.ValueKind == JsonValueKind.Null)
            return "";
        if (desc.ValueKind == JsonValueKind.String)
            return desc.GetString() ?? "";
        // Jira Cloud returns Atlassian Document Format (ADF), not plain text.
        return JiraAdf.ToPlainText(desc);
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";
}
