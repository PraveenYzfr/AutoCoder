namespace AutoCoder.Abstractions;

/// <summary>Normalized ticket from any tracker (Jira-shaped for v1).</summary>
public sealed class Ticket
{
    public required string Key { get; init; }
    public required string Summary { get; init; }
    public string Description { get; init; } = "";
    public string Status { get; init; } = "";
    public IReadOnlyList<string> Labels { get; init; } = [];
    public string? Assignee { get; init; }
    public string? ProjectKey { get; init; }
    public string? IssueType { get; init; }
    public string? Priority { get; init; }
    public IReadOnlyList<TicketComment> Comments { get; init; } = [];
}

public sealed class TicketComment
{
    public string Author { get; init; } = "";
    public string Body { get; init; } = "";
}

public sealed class TicketWriteback
{
    public required string TicketKey { get; init; }
    public string? NewStatus { get; init; }
    public string? Comment { get; init; }
    public IReadOnlyList<string> LabelsToAdd { get; init; } = [];
}
