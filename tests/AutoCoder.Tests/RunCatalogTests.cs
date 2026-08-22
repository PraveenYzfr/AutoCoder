using AutoCoder.Core.Dashboard;

namespace AutoCoder.Tests;

public sealed class RunCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ac-runs", Guid.NewGuid().ToString("N"));

    public RunCatalogTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Parses_coding_progress_and_model_roles()
    {
        var dir = Path.Combine(_root, "run-1");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "run.log"), """
            {"ts":"2026-08-22T10:00:00Z","event":"run.started","runId":"run-1","ticket":"AC-9","pipeline":"fix-bug","dryRun":false}
            {"ts":"2026-08-22T10:00:01Z","event":"step.started","step":"ScoutRepo"}
            {"ts":"2026-08-22T10:00:02Z","event":"llm.call","role":"scout","tier":"cheap","provider":"deepseek","model":"deepseek-v4-flash","prompt":10,"completion":20,"usd":0.001}
            {"ts":"2026-08-22T10:00:03Z","event":"step.succeeded","step":"ScoutRepo","ms":2000}
            {"ts":"2026-08-22T10:00:04Z","event":"llm.call","role":"planning","tier":"costly","provider":"anthropic","model":"claude-sonnet-5","prompt":100,"completion":200,"usd":0.02}
            {"ts":"2026-08-22T10:00:05Z","event":"agent.started","provider":"deepseek","model":"deepseek-v4-flash","maxTurns":40}
            {"ts":"2026-08-22T10:00:06Z","event":"agent.turn","turn":1,"maxTurns":40}
            {"ts":"2026-08-22T10:00:07Z","event":"llm.call","role":"coding","tier":"cheap","provider":"deepseek","model":"deepseek-v4-flash","prompt":50,"completion":50,"usd":0.002}
            {"ts":"2026-08-22T10:00:08Z","event":"agent.tool","tool":"write_file","path":"src/app.js","turn":1,"toolCalls":1}
            {"ts":"2026-08-22T10:00:09Z","event":"agent.tool","tool":"finish","path":"","turn":1,"toolCalls":2}
            {"ts":"2026-08-22T10:00:10Z","event":"run.succeeded","tokens":430,"usd":0.023,"toolCalls":2}
            """);

        var detail = RunCatalog.Get(_root, "run-1");
        Assert.NotNull(detail);
        Assert.Equal("AC-9", detail.Ticket);
        Assert.Equal("succeeded", detail.Status);
        Assert.Equal(3, detail.Models.Count);
        Assert.Contains(detail.Models, m => m.Role == "planning" && m.Provider == "anthropic");
        Assert.Contains(detail.Models, m => m.Role == "coding" && m.Provider == "deepseek");
        Assert.NotNull(detail.Coding);
        Assert.Equal(1, detail.Coding.Turns);
        Assert.Equal(40, detail.Coding.MaxTurns);
        Assert.True(detail.Coding.Finished);
        Assert.Contains("src/app.js", detail.Coding.Files);
        Assert.Contains("plan: anthropic/claude-sonnet-5", detail.ToSummary().ModelMix);
        Assert.Contains(detail.Journey, j => j.Label == "Take Jira ticket");
        var list = RunCatalog.List(_root);
        Assert.Single(list);
        Assert.Equal(1, list[0].FilesWritten);
    }

    [Fact]
    public void Surfaces_pr_link_and_marks_open_pr_done()
    {
        var dir = Path.Combine(_root, "pr-run");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "run.log"), """
            {"ts":"2026-08-22T10:00:00Z","event":"run.started","runId":"pr-run","ticket":"AC-2","pipeline":"fix-bug"}
            {"ts":"2026-08-22T10:00:01Z","event":"pr.opened","url":"https://github.com/PraveenYzfr/SimpleApp/pull/7"}
            {"ts":"2026-08-22T10:00:02Z","event":"run.succeeded"}
            """);

        var detail = RunCatalog.Get(_root, "pr-run");
        Assert.Equal("https://github.com/PraveenYzfr/SimpleApp/pull/7", detail!.PrUrl);
        Assert.Equal("PR opened", detail.NowLabel);
        Assert.Contains(detail.Journey, j => j.Label == "Open PR" && j.State == "done");
        Assert.Equal(detail.PrUrl, RunCatalog.List(_root).Single(s => s.RunId == "pr-run").PrUrl);
    }

    [Fact]
    public void Half_written_run_is_running_not_an_error()
    {
        var dir = Path.Combine(_root, "live");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "run.log"), """
            {"ts":"2026-08-22T10:00:00Z","event":"run.started","runId":"live","ticket":"AC-1","pipeline":"fix-bug"}
            {"ts":"2026-08-22T10:00:01Z","event":"step.started","step":"AgenticImplement"}
            {"event":"torn
            """);

        var detail = RunCatalog.Get(_root, "live");
        Assert.NotNull(detail);
        Assert.Equal("running", detail.Status);
        Assert.Equal("AgenticImplement", detail.LastStep);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* ignore */ }
    }
}
