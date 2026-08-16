namespace AutoCoder.Abstractions;

/// <summary>Reads tickets and writes status/comments back to the tracker.</summary>
public interface ITicketSource
{
    Task<Ticket> FetchAsync(string ticketKey, CancellationToken cancellationToken = default);

    Task WritebackAsync(TicketWriteback writeback, CancellationToken cancellationToken = default);
}
