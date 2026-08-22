using AutoCoder.Abstractions;
using AutoCoder.Abstractions.Config;

namespace AutoCoder.Core.Runs;

/// <summary>Per-run spend tracker. Ambient so LLM/tool clients can record without a context argument.</summary>
public sealed class RunBudget : IDisposable
{
    private static readonly AsyncLocal<RunBudget?> Ambient = new();

    public static RunBudget? Current => Ambient.Value;

    public PipelineContext Context => _context;

    private readonly PipelineContext _context;
    private readonly LimitsOptions _limits;
    private readonly RunBudget? _previous;

    private RunBudget(PipelineContext context, LimitsOptions limits, RunBudget? previous)
    {
        _context = context;
        _limits = limits;
        _previous = previous;
    }

    public static RunBudget Enter(PipelineContext context, LimitsOptions? limits)
    {
        var budget = new RunBudget(context, limits ?? new LimitsOptions(), Ambient.Value);
        Ambient.Value = budget;
        return budget;
    }

    public void AddLlm(int promptTokens, int completionTokens, decimal usd)
    {
        var spend = _context.Spend;
        spend.PromptTokens += Math.Max(0, promptTokens);
        spend.CompletionTokens += Math.Max(0, completionTokens);
        spend.EstimatedUsd += Math.Max(0, usd);
        try
        {
            ThrowIfExceeded();
        }
        catch (Exception ex)
        {
            Logging.RunLog.Event("limits.exceeded", _context, Microsoft.Extensions.Logging.LogLevel.Error, ex,
                ("promptTokens", spend.PromptTokens),
                ("completionTokens", spend.CompletionTokens),
                ("usd", spend.EstimatedUsd),
                ("toolCalls", spend.ToolCalls));
            throw;
        }
    }

    public void AddToolCalls(int count = 1)
    {
        _context.Spend.ToolCalls += Math.Max(1, count);
        try
        {
            ThrowIfExceeded();
        }
        catch (Exception ex)
        {
            Logging.RunLog.Event("limits.exceeded", _context, Microsoft.Extensions.Logging.LogLevel.Error, ex,
                ("toolCalls", _context.Spend.ToolCalls));
            throw;
        }
    }

    public void ThrowIfExceeded()
    {
        var spend = _context.Spend;
        if (_limits.MaxTokensPerRun > 0 && spend.TotalTokens > _limits.MaxTokensPerRun)
        {
            throw new InvalidOperationException(
                $"Run token cap exceeded ({spend.TotalTokens}/{_limits.MaxTokensPerRun}).");
        }

        if (_limits.MaxUsdPerRun > 0 && spend.EstimatedUsd > _limits.MaxUsdPerRun)
        {
            throw new InvalidOperationException(
                $"Run USD cap exceeded (${spend.EstimatedUsd:F4}/${_limits.MaxUsdPerRun:F2}).");
        }

        if (_limits.MaxToolCalls > 0 && spend.ToolCalls > _limits.MaxToolCalls)
        {
            throw new InvalidOperationException(
                $"Run tool-call cap exceeded ({spend.ToolCalls}/{_limits.MaxToolCalls}).");
        }
    }

    public void Dispose() => Ambient.Value = _previous;
}
