using AutoCoder.Core.Runs;

namespace AutoCoder.Tests;

public sealed class TicketRetryTrackerTests : IDisposable
{
    private readonly string _dir;

    public TicketRetryTrackerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "autocoder-retry", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public void Increments_across_calls_and_persists_to_disk()
    {
        Assert.Equal(1, TicketRetryTracker.IncrementAndGet(_dir, "AC-1"));
        Assert.Equal(2, TicketRetryTracker.IncrementAndGet(_dir, "AC-1"));
        Assert.Equal(3, TicketRetryTracker.IncrementAndGet(_dir, "AC-1"));
        Assert.Equal(3, TicketRetryTracker.Peek(_dir, "AC-1"));
    }

    [Fact]
    public void Different_tickets_have_independent_counters()
    {
        TicketRetryTracker.IncrementAndGet(_dir, "AC-1");
        TicketRetryTracker.IncrementAndGet(_dir, "AC-1");
        Assert.Equal(1, TicketRetryTracker.IncrementAndGet(_dir, "AC-2"));
    }

    [Fact]
    public void Reset_clears_the_counter_so_the_next_attempt_starts_at_one()
    {
        TicketRetryTracker.IncrementAndGet(_dir, "AC-9");
        TicketRetryTracker.Reset(_dir, "AC-9");
        Assert.Equal(0, TicketRetryTracker.Peek(_dir, "AC-9"));
        Assert.Equal(1, TicketRetryTracker.IncrementAndGet(_dir, "AC-9"));
    }

    [Fact]
    public void Reset_on_a_ticket_with_no_counter_is_a_no_op()
    {
        TicketRetryTracker.Reset(_dir, "AC-nonexistent");
        Assert.Equal(0, TicketRetryTracker.Peek(_dir, "AC-nonexistent"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* ignore */ }
    }
}
