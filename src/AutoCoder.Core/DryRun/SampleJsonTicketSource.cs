using System.Text.Json;
using AutoCoder.Abstractions;

namespace AutoCoder.Core.DryRun;

public sealed class SampleJsonTicketSource : ITicketSource
{
    private readonly string _ticketPath;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public SampleJsonTicketSource(string ticketPath)
    {
        _ticketPath = ticketPath;
    }

    public async Task<Ticket> FetchAsync(string ticketKey, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(_ticketPath);
        var dto = await JsonSerializer.DeserializeAsync<SampleTicketDto>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException($"Could not parse ticket JSON at '{_ticketPath}'.");

        if (!string.IsNullOrWhiteSpace(ticketKey)
            && !string.Equals(dto.Key, ticketKey, StringComparison.OrdinalIgnoreCase)
            && ticketKey is not "from-file")
        {
            throw new InvalidOperationException(
                $"Ticket key mismatch: file has '{dto.Key}', requested '{ticketKey}'.");
        }

        return new Ticket
        {
            Key = dto.Key,
            Summary = dto.Summary,
            Description = dto.Description ?? "",
            Status = dto.Status ?? "",
            Labels = dto.Labels ?? [],
            Assignee = dto.Assignee,
            ProjectKey = dto.ProjectKey,
            IssueType = dto.IssueType,
            Priority = dto.Priority,
            Comments = (dto.Comments ?? [])
                .Select(c => new TicketComment { Author = c.Author ?? "", Body = c.Body ?? "" })
                .ToList()
        };
    }

    public Task WritebackAsync(TicketWriteback writeback, CancellationToken cancellationToken = default)
    {
        Console.WriteLine();
        Console.WriteLine("[dry-run] Ticket writeback (not sent):");
        Console.WriteLine($"  key:     {writeback.TicketKey}");
        Console.WriteLine($"  status:  {writeback.NewStatus ?? "(unchanged)"}");
        Console.WriteLine($"  comment: {writeback.Comment}");
        return Task.CompletedTask;
    }

    private sealed class SampleTicketDto
    {
        public string Key { get; set; } = "";
        public string Summary { get; set; } = "";
        public string? Description { get; set; }
        public string? Status { get; set; }
        public List<string>? Labels { get; set; }
        public string? Assignee { get; set; }
        public string? ProjectKey { get; set; }
        public string? IssueType { get; set; }
        public string? Priority { get; set; }
        public List<SampleCommentDto>? Comments { get; set; }
    }

    private sealed class SampleCommentDto
    {
        public string? Author { get; set; }
        public string? Body { get; set; }
    }
}
