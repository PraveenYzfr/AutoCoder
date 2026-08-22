using AutoCoder.Abstractions;

namespace AutoCoder.Core.Llm;

/// <summary>
/// Cheap vs costly, with optional per-role overrides from the dashboard.
/// Overrides are snapshotted when the provider is created (start of a run).
/// </summary>
public sealed class RoutedLlmProvider : ILlmProvider
{
    private readonly ILlmProvider _cheap;
    private readonly ILlmProvider _costly;
    private readonly IReadOnlyDictionary<string, string> _roleTiers;
    private readonly IReadOnlyDictionary<string, RoleModelOverride> _overrides;
    private readonly Func<string, string, ILlmProvider>? _createOverride;
    private readonly Dictionary<string, ILlmProvider> _overrideBackends = new(StringComparer.OrdinalIgnoreCase);

    public RoutedLlmProvider(
        ILlmProvider cheap,
        ILlmProvider costly,
        IReadOnlyDictionary<string, string>? roleTiers = null,
        IReadOnlyDictionary<string, RoleModelOverride>? overrides = null,
        Func<string, string, ILlmProvider>? createOverride = null)
    {
        _cheap = cheap;
        _costly = costly;
        _roleTiers = roleTiers ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _overrides = overrides ?? new Dictionary<string, RoleModelOverride>(StringComparer.OrdinalIgnoreCase);
        _createOverride = createOverride;
    }

    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        var role = (request.ModelRole ?? "").Trim();
        if (_createOverride is not null
            && _overrides.TryGetValue(role, out var over)
            && !string.IsNullOrWhiteSpace(over.Provider)
            && !string.IsNullOrWhiteSpace(over.Model))
        {
            LlmCallContext.CurrentRole = role;
            LlmCallContext.CurrentTier = "override";
            Console.WriteLine($"[llm] role={role} tier=override {over.Provider}/{over.Model}");
            var key = $"{over.Provider}/{over.Model}";
            if (!_overrideBackends.TryGetValue(key, out var backend))
            {
                backend = _createOverride(over.Provider, over.Model);
                _overrideBackends[key] = backend;
            }

            return backend.CompleteAsync(request, cancellationToken);
        }

        var cheap = IsCheap(request.ModelRole, _roleTiers);
        LlmCallContext.CurrentRole = request.ModelRole;
        LlmCallContext.CurrentTier = cheap ? "cheap" : "costly";
        Console.WriteLine($"[llm] role={request.ModelRole} tier={(cheap ? "cheap" : "costly")}");
        return (cheap ? _cheap : _costly).CompleteAsync(request, cancellationToken);
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
