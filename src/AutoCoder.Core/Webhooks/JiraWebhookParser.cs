using System.Text.Json;
using AutoCoder.Abstractions;

namespace AutoCoder.Core.Webhooks;

public sealed class JiraWebhookParseResult
{
    public required Ticket Ticket { get; init; }
    public string? WebhookEvent { get; init; }
    public string? StatusName { get; init; }
}

public static class JiraWebhookParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static bool TryParse(string json, out JiraWebhookParseResult? result, out string? error)
    {
        result = null;
        error = null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("issue", out var issue))
            {
                error = "Payload missing 'issue' (not a Jira issue webhook).";
                return false;
            }

            var key = issue.TryGetProperty("key", out var keyEl) ? keyEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(key))
            {
                error = "Issue key missing.";
                return false;
            }

            var fields = issue.TryGetProperty("fields", out var f) ? f : default;
            var summary = GetString(fields, "summary") ?? key;
            var description = ExtractDescription(fields);
            var statusName = fields.ValueKind == JsonValueKind.Object
                && fields.TryGetProperty("status", out var status)
                && status.TryGetProperty("name", out var statusNameEl)
                    ? statusNameEl.GetString()
                    : null;

            var labels = new List<string>();
            if (fields.ValueKind == JsonValueKind.Object
                && fields.TryGetProperty("labels", out var labelsEl)
                && labelsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var label in labelsEl.EnumerateArray())
                {
                    var value = label.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                        labels.Add(value);
                }
            }

            string? assignee = null;
            if (fields.ValueKind == JsonValueKind.Object
                && fields.TryGetProperty("assignee", out var assigneeEl)
                && assigneeEl.ValueKind == JsonValueKind.Object)
            {
                assignee = GetString(assigneeEl, "displayName") ?? GetString(assigneeEl, "emailAddress");
            }

            var projectKey = fields.ValueKind == JsonValueKind.Object
                && fields.TryGetProperty("project", out var project)
                && project.TryGetProperty("key", out var pk)
                    ? pk.GetString()
                    : null;

            var issueType = fields.ValueKind == JsonValueKind.Object
                && fields.TryGetProperty("issuetype", out var it)
                && it.TryGetProperty("name", out var itn)
                    ? itn.GetString()
                    : null;

            var priority = fields.ValueKind == JsonValueKind.Object
                && fields.TryGetProperty("priority", out var pr)
                && pr.TryGetProperty("name", out var prn)
                    ? prn.GetString()
                    : null;

            var webhookEvent = root.TryGetProperty("webhookEvent", out var we) ? we.GetString() : null;

            result = new JiraWebhookParseResult
            {
                WebhookEvent = webhookEvent,
                StatusName = statusName,
                Ticket = new Ticket
                {
                    Key = key,
                    Summary = summary,
                    Description = description,
                    Status = statusName ?? "",
                    Labels = labels,
                    Assignee = assignee,
                    ProjectKey = projectKey,
                    IssueType = issueType,
                    Priority = priority,
                    Comments = []
                }
            };
            return true;
        }
        catch (JsonException ex)
        {
            error = $"Invalid JSON: {ex.Message}";
            return false;
        }
    }

    private static string? GetString(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object)
            return null;
        return parent.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;
    }

    private static string ExtractDescription(JsonElement fields)
    {
        if (fields.ValueKind != JsonValueKind.Object || !fields.TryGetProperty("description", out var desc))
            return "";

        if (desc.ValueKind == JsonValueKind.String)
            return desc.GetString() ?? "";

        // ADF or other structured description — keep a compact JSON snapshot for the planner.
        return desc.ValueKind is JsonValueKind.Object or JsonValueKind.Array
            ? desc.GetRawText()
            : "";
    }
}
