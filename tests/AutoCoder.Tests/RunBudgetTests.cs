using AutoCoder.Abstractions.Config;
using AutoCoder.Core.Runs;

namespace AutoCoder.Tests;

public sealed class RunBudgetTests
{
    [Fact]
    public void Throws_when_token_cap_exceeded()
    {
        var ctx = TestContext.New();
        using var budget = RunBudget.Enter(ctx, new LimitsOptions
        {
            MaxTokensPerRun = 100,
            MaxUsdPerRun = 0,
            MaxToolCalls = 0
        });
        budget.AddLlm(40, 40, 0m);
        var ex = Assert.Throws<InvalidOperationException>(() => budget.AddLlm(30, 0, 0m));
        Assert.Contains("token cap", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(110, ctx.Spend.TotalTokens);
    }

    [Fact]
    public void Throws_when_usd_cap_exceeded()
    {
        var ctx = TestContext.New();
        using var budget = RunBudget.Enter(ctx, new LimitsOptions
        {
            MaxTokensPerRun = 0,
            MaxUsdPerRun = 1.00m,
            MaxToolCalls = 0
        });
        var ex = Assert.Throws<InvalidOperationException>(() => budget.AddLlm(0, 0, 1.50m));
        Assert.Contains("USD cap", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Throws_when_tool_cap_exceeded()
    {
        var ctx = TestContext.New();
        using var budget = RunBudget.Enter(ctx, new LimitsOptions
        {
            MaxTokensPerRun = 0,
            MaxUsdPerRun = 0,
            MaxToolCalls = 2
        });
        budget.AddToolCalls(2);
        var ex = Assert.Throws<InvalidOperationException>(() => budget.AddToolCalls(1));
        Assert.Contains("tool-call cap", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, ctx.Spend.ToolCalls);
    }

    [Fact]
    public void Zero_caps_are_unlimited()
    {
        var ctx = TestContext.New();
        using var budget = RunBudget.Enter(ctx, new LimitsOptions
        {
            MaxTokensPerRun = 0,
            MaxUsdPerRun = 0,
            MaxToolCalls = 0
        });
        budget.AddLlm(1_000_000, 1_000_000, 50m);
        budget.AddToolCalls(500);
        Assert.Equal(2_000_000, ctx.Spend.TotalTokens);
    }
}
