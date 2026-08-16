using AutoCoder.Abstractions;

namespace AutoCoder.Core.Webhooks;

/// <summary>Serves a ticket already parsed from a webhook payload (no remote fetch).</summary>
public sealed class InMemoryTicketSource : ITicketSource
{
    private readonly Ticket _ticket;

    public InMemoryTicketSource(Ticket ticket) => _ticket = ticket;

    public Task<Ticket> FetchAsync(string ticketKey, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(ticketKey, _ticket.Key, StringComparison.OrdinalIgnoreCase)
            && ticketKey is not "from-file" and not "from-webhook")
        {
            throw new InvalidOperationException(
                $"In-memory ticket is '{_ticket.Key}', requested '{ticketKey}'.");
        }

        return Task.FromResult(_ticket);
    }

    public Task WritebackAsync(TicketWriteback writeback, CancellationToken cancellationToken = default)
    {
        Console.WriteLine();
        Console.WriteLine("[webhook] Ticket writeback (not sent until live Jira adapter):");
        Console.WriteLine($"  key:     {writeback.TicketKey}");
        Console.WriteLine($"  status:  {writeback.NewStatus ?? "(unchanged)"}");
        Console.WriteLine($"  comment: {writeback.Comment}");
        return Task.CompletedTask;
    }
}
