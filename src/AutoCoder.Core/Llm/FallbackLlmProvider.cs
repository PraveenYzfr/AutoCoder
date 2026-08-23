using System.Diagnostics;
using AutoCoder.Abstractions;
using AutoCoder.Core.Logging;
using AutoCoder.Core.Runs;
using Microsoft.Extensions.Logging;

namespace AutoCoder.Core.Llm;

/// <summary>
/// Tries an ordered list of providers for one cost tier (cheap or costly) and falls back to the next
/// on any failure — item 10 in CLAUDE-AUTOCODER.md. Same-provider retry (429/5xx/timeout backoff)
/// already happens inside each provider's own HTTP call via <see cref="Resilience.TransientRetry"/>;
/// by the time an exception reaches here, that provider has already had its fair shot. This class only
/// decides whether to move on to the next provider in the tier, never automatically escalating outside
/// the tier's configured chain (deepseek/groq only — never Anthropic/OpenAI/Gemini, which are the
/// benchmark budget, not a safety net).
/// </summary>
public sealed class FallbackLlmProvider : ILlmProvider
{
    private readonly string _tier;
    private readonly IReadOnlyList<(string Label, ILlmProvider Provider)> _chain;

    public FallbackLlmProvider(string tier, IReadOnlyList<(string Label, ILlmProvider Provider)> chain)
    {
        if (chain.Count == 0)
            throw new ArgumentException("At least one provider is required.", nameof(chain));
        _tier = tier;
        _chain = chain;
    }

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        Exception? last = null;
        for (var i = 0; i < _chain.Count; i++)
        {
            var (label, provider) = _chain[i];
            var isLast = i == _chain.Count - 1;
            var sw = Stopwatch.StartNew();
            try
            {
                var response = await provider.CompleteAsync(request, cancellationToken);
                LogAttempt(label, request.ModelRole, "succeeded", null, sw.Elapsed);
                return response;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
                LogAttempt(label, request.ModelRole, "failed", ex, sw.Elapsed);
                if (!isLast)
                {
                    Console.WriteLine(
                        $"[llm] {_tier} tier: {label} failed ({Describe(ex)}); falling back to "
                        + $"{_chain[i + 1].Label}.");
                }
            }
        }

        var transient = LlmFailureClassifier.IsTransient(last!);
        throw new LlmTierExhaustedException(
            _tier,
            transient,
            $"All providers in the '{_tier}' tier failed. Last error: {last!.Message}",
            last!);
    }

    private void LogAttempt(string provider, string role, string outcome, Exception? error, TimeSpan elapsed)
    {
        var ctx = RunBudget.Current?.Context;
        if (ctx is null)
            return;

        var statusCode = error is LlmProviderException { StatusCode: { } code } ? code : (int?)null;
        RunLog.Event(
            "llm.attempt",
            ctx,
            error is null ? LogLevel.Information : LogLevel.Warning,
            fields:
            [
                ("tier", _tier),
                ("provider", provider),
                ("role", role),
                ("outcome", outcome),
                ("statusCode", statusCode),
                ("error", error is null ? null : Truncate(error.Message, 300)),
                ("ms", (int)elapsed.TotalMilliseconds)
            ]);
    }

    private static string Describe(Exception ex) => ex is LlmProviderException p
        ? p.IsEmptyContent ? "empty content" : $"HTTP {p.StatusCode}"
        : ex.Message;

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";
}
