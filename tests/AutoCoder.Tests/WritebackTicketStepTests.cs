using AutoCoder.Abstractions;
using AutoCoder.Core.Pipelines;
using AutoCoder.Core.Runs;

namespace AutoCoder.Tests;

public sealed class WritebackTicketStepTests : IDisposable
{
    private readonly string _artifacts;

    public WritebackTicketStepTests()
    {
        _artifacts = Path.Combine(Path.GetTempPath(), "autocoder-writeback", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_artifacts);
    }

    [Fact]
    public async Task Transient_failure_under_the_cap_sends_the_ticket_back_to_the_poller()
    {
        var source = new RecordingTicketSource();
        var context = Context(failureReason: "deepseek 503", transient: true, retryStatus: "AssignedToAgent");

        await new WritebackTicketStep(source).ExecuteAsync(context);

        Assert.Equal("AssignedToAgent", source.LastWriteback!.NewStatus);
        Assert.Contains("attempt 1/3", source.LastWriteback!.Comment);
        Assert.Contains("autocoder:retrying", source.LastWriteback!.LabelsToAdd);
    }

    [Fact]
    public async Task Transient_failure_at_the_cap_goes_to_agent_failure_and_resets_the_counter()
    {
        var source = new RecordingTicketSource();

        // Three separate runs of the same ticket, each hitting a transient failure.
        await new WritebackTicketStep(source).ExecuteAsync(
            Context(failureReason: "deepseek 503", transient: true, retryStatus: "AssignedToAgent"));
        await new WritebackTicketStep(source).ExecuteAsync(
            Context(failureReason: "deepseek 503", transient: true, retryStatus: "AssignedToAgent"));
        await new WritebackTicketStep(source).ExecuteAsync(
            Context(failureReason: "deepseek 503", transient: true, retryStatus: "AssignedToAgent"));

        Assert.Equal("Agent Failure", source.LastWriteback!.NewStatus);
        Assert.Contains("after 3 transient retries", source.LastWriteback!.Comment);
        Assert.Equal(0, TicketRetryTracker.Peek(_artifacts, "AC-101"));
    }

    [Fact]
    public async Task Permanent_failure_goes_straight_to_agent_failure_without_touching_the_retry_counter()
    {
        var source = new RecordingTicketSource();
        var context = Context(failureReason: "Build failed", transient: false);

        await new WritebackTicketStep(source).ExecuteAsync(context);

        Assert.Equal("Agent Failure", source.LastWriteback!.NewStatus);
        Assert.Contains("autocoder:failed", source.LastWriteback!.LabelsToAdd);
        Assert.Equal(0, TicketRetryTracker.Peek(_artifacts, "AC-101"));
    }

    [Fact]
    public async Task Success_resets_a_stale_transient_counter_from_an_earlier_failed_run()
    {
        var source = new RecordingTicketSource();
        TicketRetryTracker.IncrementAndGet(_artifacts, "AC-101");
        TicketRetryTracker.IncrementAndGet(_artifacts, "AC-101");

        await new WritebackTicketStep(source).ExecuteAsync(Context(failureReason: null, transient: false));

        Assert.Equal("In Review", source.LastWriteback!.NewStatus);
        Assert.Contains("autocoder:done", source.LastWriteback!.LabelsToAdd);
        Assert.Equal(0, TicketRetryTracker.Peek(_artifacts, "AC-101"));
    }

    private PipelineContext Context(string? failureReason, bool transient, string? retryStatus = null) => new()
    {
        RunId = "test-run",
        PipelineName = "fix-bug",
        ArtifactsDirectory = _artifacts,
        DryRun = false,
        Ticket = new Ticket { Key = "AC-101", Summary = "Test ticket" },
        FailureReason = failureReason,
        FailureIsTransient = transient,
        RetryStatus = retryStatus,
        FailedStatus = "Agent Failure",
        DoneStatus = "In Review"
    };

    private sealed class RecordingTicketSource : ITicketSource
    {
        public TicketWriteback? LastWriteback { get; private set; }

        public Task<Ticket> FetchAsync(string ticketKey, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not needed for writeback tests.");

        public Task WritebackAsync(TicketWriteback writeback, CancellationToken cancellationToken = default)
        {
            LastWriteback = writeback;
            return Task.CompletedTask;
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_artifacts, recursive: true); } catch { /* ignore */ }
    }
}
