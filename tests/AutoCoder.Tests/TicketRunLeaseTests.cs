using AutoCoder.Core.Runs;

namespace AutoCoder.Tests;

public sealed class TicketRunLeaseTests : IDisposable
{
    private readonly string _dir;

    public TicketRunLeaseTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "autocoder-lease", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public void First_acquire_wins_second_is_blocked()
    {
        Assert.True(TicketRunLease.TryAcquire(_dir, "AC-1", out _));
        Assert.False(TicketRunLease.TryAcquire(_dir, "AC-1", out var skip));
        Assert.Contains("lease held until", skip);
    }

    [Fact]
    public void Different_tickets_do_not_block_each_other()
    {
        Assert.True(TicketRunLease.TryAcquire(_dir, "AC-1", out _));
        Assert.True(TicketRunLease.TryAcquire(_dir, "AC-2", out _));
    }

    [Fact]
    public void Expired_lease_can_be_reacquired()
    {
        Assert.True(TicketRunLease.TryAcquire(_dir, "AC-9", out _));
        var path = Path.Combine(_dir, "leases", "AC-9.lease");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-46));
        Assert.True(TicketRunLease.TryAcquire(_dir, "AC-9", out _));
    }

    [Fact]
    public void Release_allows_reacquire()
    {
        Assert.True(TicketRunLease.TryAcquire(_dir, "SCRUM-7", out _));
        TicketRunLease.Release(_dir, "SCRUM-7");
        Assert.True(TicketRunLease.TryAcquire(_dir, "SCRUM-7", out _));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* ignore */ }
    }
}
