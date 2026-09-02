using AutoCoder.Core.GitHub;

namespace AutoCoder.Tests;

public sealed class AutocoderBranchNamingTests
{
    [Fact]
    public void ForTicket_includes_ticket_key_and_8_char_hex_suffix()
    {
        var branch = AutocoderBranchNaming.ForTicket("SCRUM-9", "2026-09-02T12-00-00-ab12-scrum-9");

        Assert.StartsWith("autocoder/scrum-9-", branch, StringComparison.Ordinal);
        Assert.Matches(@"^autocoder/scrum-9-[a-f0-9]{8}$", branch);
    }

    [Fact]
    public void ForTicket_is_deterministic_for_same_run()
    {
        const string runId = "2026-09-02T12-00-00-ab12-scrum-9";
        var a = AutocoderBranchNaming.ForTicket("SCRUM-9", runId);
        var b = AutocoderBranchNaming.ForTicket("SCRUM-9", runId);
        Assert.Equal(a, b);
    }

    [Fact]
    public void ForTicket_differs_across_runs_for_same_ticket()
    {
        var a = AutocoderBranchNaming.ForTicket("SCRUM-9", "run-a");
        var b = AutocoderBranchNaming.ForTicket("SCRUM-9", "run-b");
        Assert.NotEqual(a, b);
    }
}
