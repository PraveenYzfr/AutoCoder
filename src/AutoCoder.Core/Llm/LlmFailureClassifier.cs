namespace AutoCoder.Core.Llm;

/// <summary>
/// A single provider call failed with a classifiable reason (HTTP status, or "200 but unusable").
/// Thrown by the concrete provider clients instead of a plain <see cref="InvalidOperationException"/>
/// so <see cref="LlmFailureClassifier"/> and <see cref="FallbackLlmProvider"/> can decide whether to
/// retry the same provider, switch providers, or give up — without parsing error message text.
/// </summary>
public sealed class LlmProviderException : Exception
{
    public string Provider { get; }
    public int? StatusCode { get; }
    public bool IsEmptyContent { get; }

    public LlmProviderException(
        string provider,
        string message,
        int? statusCode = null,
        bool isEmptyContent = false,
        Exception? inner = null)
        : base(message, inner)
    {
        Provider = provider;
        StatusCode = statusCode;
        IsEmptyContent = isEmptyContent;
    }
}

/// <summary>
/// Every provider configured for a cost tier (cheap or costly) was tried and none produced a usable
/// response. <see cref="Transient"/> reflects the last attempt: true if it looks like a blip worth
/// retrying the whole ticket later (rate limit, 5xx, timeout), false if it looks permanent (bad
/// request, auth, empty content) — see item 10 in CLAUDE-AUTOCODER.md.
/// </summary>
public sealed class LlmTierExhaustedException : Exception
{
    public string Tier { get; }
    public bool Transient { get; }

    public LlmTierExhaustedException(string tier, bool transient, string message, Exception inner)
        : base(message, inner)
    {
        Tier = tier;
        Transient = transient;
    }
}

/// <summary>
/// Retry policy for LLM failures (item 10 in CLAUDE-AUTOCODER.md):
/// 429 / 5xx / 408 / timeouts are transient — worth retrying (same provider, then a different one,
/// then the whole ticket later). 400 / 401 / 403 and "200 but empty content" are permanent — retrying
/// them is pointless (a temperature field will not stop being deprecated on attempt two), so the right
/// response is to switch providers immediately, not to retry.
/// </summary>
public static class LlmFailureClassifier
{
    private static readonly HashSet<int> RetryableStatusCodes = [408, 429, 500, 502, 503, 504, 529];

    public static bool IsTransient(Exception ex)
    {
        for (var cur = ex; cur is not null; cur = cur.InnerException)
        {
            if (cur is LlmTierExhaustedException tier)
                return tier.Transient;

            if (cur is LlmProviderException provider)
            {
                return !provider.IsEmptyContent
                       && provider.StatusCode is { } code
                       && RetryableStatusCodes.Contains(code);
            }

            if (Resilience.TransientRetry.IsTransient(cur))
                return true;
        }

        return false;
    }
}
