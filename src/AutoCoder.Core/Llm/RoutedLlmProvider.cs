using AutoCoder.Abstractions;

namespace AutoCoder.Core.Llm;

/// <summary>
/// Cheap (DeepSeek Flash) vs costly (Claude Sonnet / GPT-4o / Gemini Pro).
/// Summarize, scout, and coding stay cheap; planning/thinking/decisions use costly.
/// </summary>
public sealed class RoutedLlmProvider : ILlmProvider
{
    private readonly ILlmProvider _cheap;
    private readonly ILlmProvider _costly;
    private readonly IReadOnlyDictionary<string, string> _roleTiers;

    public RoutedLlmProvider(
        ILlmProvider cheap,
        ILlmProvider costly,
        IReadOnlyDictionary<string, string>? roleTiers = null)
    {
        _cheap = cheap;
        _costly = costly;
        _roleTiers = roleTiers ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        var cheap = IsCheap(request.ModelRole, _roleTiers);
        Console.WriteLine($"[llm] role={request.ModelRole} tier={(cheap ? "cheap" : "costly")}");
        var backend = cheap ? _cheap : _costly;
        return backend.CompleteAsync(request, cancellationToken);
    }

    public static bool IsCheap(string? modelRole) => IsCheap(modelRole, null);

    public static bool IsCheap(string? modelRole, IReadOnlyDictionary<string, string>? roleTiers)
    {
        var role = (modelRole ?? "").Trim().ToLowerInvariant();
        if (roleTiers is not null
            && roleTiers.TryGetValue(role, out var tier)
            && !string.IsNullOrWhiteSpace(tier))
        {
            return tier.Trim().Equals("cheap", StringComparison.OrdinalIgnoreCase);
        }

        return role is "cheap" or "scout" or "summarize" or "comment" or "coding";
    }
}
