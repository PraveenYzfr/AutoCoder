using AutoCoder.Core.Llm;

namespace AutoCoder.Tests;

[Collection(ProcessEnvStateCollection.Name)]
public sealed class LlmDailyBudgetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ac-budget", Guid.NewGuid().ToString("N"));
    private readonly string? _prevRoot;
    private readonly string? _prevCap;

    public LlmDailyBudgetTests()
    {
        Directory.CreateDirectory(_root);
        _prevRoot = Environment.GetEnvironmentVariable("AUTOCODER_CONTAINER_WORKSPACE_ROOT");
        _prevCap = Environment.GetEnvironmentVariable("AUTOCODER_LLM_DAILY_CALL_BUDGET");
        Environment.SetEnvironmentVariable("AUTOCODER_CONTAINER_WORKSPACE_ROOT", _root);
    }

    [Fact]
    public void Zero_or_unset_cap_is_unlimited_and_never_throws()
    {
        Environment.SetEnvironmentVariable("AUTOCODER_LLM_DAILY_CALL_BUDGET", "0");
        for (var i = 0; i < 50; i++)
            LlmDailyBudget.Consume();

        var (used, cap) = LlmDailyBudget.Snapshot();
        Assert.Equal(0, cap);
        Assert.Equal(0, used); // Consume() is a no-op while unlimited — nothing is written to disk.
    }

    [Fact]
    public void Consume_accumulates_and_snapshot_reflects_it()
    {
        Environment.SetEnvironmentVariable("AUTOCODER_LLM_DAILY_CALL_BUDGET", "5");
        LlmDailyBudget.Consume();
        LlmDailyBudget.Consume(2);
        var (used, cap) = LlmDailyBudget.Snapshot();
        Assert.Equal(3, used);
        Assert.Equal(5, cap);
    }

    [Fact]
    public void Consume_throws_once_the_cap_is_exceeded()
    {
        Environment.SetEnvironmentVariable("AUTOCODER_LLM_DAILY_CALL_BUDGET", "2");
        LlmDailyBudget.Consume();
        LlmDailyBudget.Consume();
        var ex = Assert.Throws<InvalidOperationException>(() => LlmDailyBudget.Consume());
        Assert.Contains("daily LLM budget exhausted", ex.Message);
        Assert.Contains("3/2", ex.Message);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("AUTOCODER_CONTAINER_WORKSPACE_ROOT", _prevRoot);
        Environment.SetEnvironmentVariable("AUTOCODER_LLM_DAILY_CALL_BUDGET", _prevCap);
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }
}
