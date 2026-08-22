using System.Net.Http.Headers;
using System.Text.Json;
using AutoCoder.Abstractions.Config;

namespace AutoCoder.Core.Llm;

public sealed record CatalogModel(string Id);

public sealed record CatalogProvider(string Name, IReadOnlyList<CatalogModel> Models, string? Error);

public static class ModelCatalog
{
    private static readonly string[] Unusable =
    [
        "whisper", "tts-", "-tts", "orpheus", "prompt-guard", "embed", "embedding",
        "dall-e", "dalle", "moderation", "transcri", "speech-to", "text-to-speech",
        "rerank", "classifier", "vision-exp"
    ];

    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);
    private static readonly object Gate = new();
    private static List<CatalogProvider>? _cache;
    private static DateTime _cacheUtc;

    public static async Task<IReadOnlyList<CatalogProvider>> ListAsync(CancellationToken cancellationToken = default)
    {
        lock (Gate)
        {
            if (_cache is not null && DateTime.UtcNow - _cacheUtc < Ttl)
                return _cache;
        }

        // Cap total wait — UI must not hang behind Cloudflare if a provider is slow.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(TimeSpan.FromSeconds(12));
        var ct = linked.Token;

        CatalogProvider[] results;
        try
        {
            results = await Task.WhenAll(
                FetchOpenAi("deepseek", "DEEPSEEK_API_KEY", "https://api.deepseek.com/models", ct),
                FetchOpenAi("groq", "GROQ_API_KEY", "https://api.groq.com/openai/v1/models", ct),
                FetchOpenAi("openai", "OPENAI_API_KEY", "https://api.openai.com/v1/models", ct),
                FetchAnthropic(ct),
                FetchGemini(ct));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            results =
            [
                new CatalogProvider("deepseek", [], "timed out"),
                new CatalogProvider("groq", [], "timed out"),
                new CatalogProvider("openai", [], "timed out"),
                new CatalogProvider("anthropic", [], "timed out"),
                new CatalogProvider("gemini", [], "timed out")
            ];
        }

        lock (Gate)
        {
            _cache = results.ToList();
            _cacheUtc = DateTime.UtcNow;
            return _cache;
        }
    }

    public static bool IsKnown(IReadOnlyList<CatalogProvider> catalog, string provider, string model) =>
        catalog.Any(p => p.Name.Equals(provider, StringComparison.OrdinalIgnoreCase)
                         && p.Models.Any(m => m.Id.Equals(model, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Keep the active config/override model selectable even when /models is empty or filtered.</summary>
    public static IReadOnlyList<CatalogProvider> EnsureCurrentOptions(
        IReadOnlyList<CatalogProvider> catalog,
        IReadOnlyList<RoleEffective> roles)
    {
        var byName = catalog.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var role in roles)
        {
            if (!byName.TryGetValue(role.Provider, out var existing))
            {
                byName[role.Provider] = new CatalogProvider(role.Provider, [new CatalogModel(role.Model)], null);
                continue;
            }

            if (existing.Models.Any(m => m.Id.Equals(role.Model, StringComparison.OrdinalIgnoreCase)))
                continue;

            var models = existing.Models.ToList();
            models.Insert(0, new CatalogModel(role.Model));
            byName[role.Provider] = existing with { Models = models, Error = null };
        }

        return ModelOverrideStore.Providers
            .Select(name => byName.TryGetValue(name, out var p) ? p : new CatalogProvider(name, [], "no API key"))
            .ToList();
    }

    public static IReadOnlyList<RoleEffective> Effective(AutoCoderOptions options)
    {
        var routing = LlmProviderFactory.Describe(options);
        var file = ModelOverrideStore.Load(options);
        return ModelOverrideStore.Roles.Select(role =>
        {
            if (file.Roles.TryGetValue(role, out var over)
                && !string.IsNullOrWhiteSpace(over.Provider)
                && !string.IsNullOrWhiteSpace(over.Model))
            {
                return new RoleEffective(role, over.Provider, over.Model, "override");
            }

            var cheap = RoutedLlmProvider.IsCheap(role, LlmProviderFactory.GetAgent(options).RoleTiers);
            if (role.Equals("coding", StringComparison.OrdinalIgnoreCase))
                return new RoleEffective(role, routing.CodingType, routing.CodingModel, "config");
            return cheap
                ? new RoleEffective(role, routing.CheapType, routing.CheapModel, "config")
                : new RoleEffective(role, routing.CostlyType, routing.CostlyModel, "config");
        }).ToList();
    }

    internal static bool IsChatModel(string id)
    {
        var n = (id ?? "").ToLowerInvariant();
        return Unusable.All(bad => !n.Contains(bad, StringComparison.Ordinal));
    }

    private static async Task<CatalogProvider> FetchOpenAi(
        string name, string keyEnv, string url, CancellationToken cancellationToken)
    {
        var key = Environment.GetEnvironmentVariable(keyEnv);
        if (string.IsNullOrWhiteSpace(key))
            return new CatalogProvider(name, [], "no API key");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var res = await http.GetAsync(url, cancellationToken);
            var raw = await res.Content.ReadAsStringAsync(cancellationToken);
            if (!res.IsSuccessStatusCode)
                return new CatalogProvider(name, [], $"HTTP {(int)res.StatusCode}");
            return new CatalogProvider(name, ReadOpenAiIds(raw), null);
        }
        catch (Exception ex)
        {
            return new CatalogProvider(name, [], ex.Message);
        }
    }

    private static async Task<CatalogProvider> FetchAnthropic(CancellationToken cancellationToken)
    {
        var key = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
            return new CatalogProvider("anthropic", [], "no API key");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            http.DefaultRequestHeaders.TryAddWithoutValidation("x-api-key", key);
            http.DefaultRequestHeaders.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            using var res = await http.GetAsync("https://api.anthropic.com/v1/models", cancellationToken);
            var raw = await res.Content.ReadAsStringAsync(cancellationToken);
            if (!res.IsSuccessStatusCode)
                return new CatalogProvider("anthropic", [], $"HTTP {(int)res.StatusCode}");
            return new CatalogProvider("anthropic", ReadOpenAiIds(raw), null);
        }
        catch (Exception ex)
        {
            return new CatalogProvider("anthropic", [], ex.Message);
        }
    }

    private static async Task<CatalogProvider> FetchGemini(CancellationToken cancellationToken)
    {
        var key = Environment.GetEnvironmentVariable("GEMINI_API_KEY")
                  ?? Environment.GetEnvironmentVariable("GOOGLE_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
            return new CatalogProvider("gemini", [], "no API key");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            using var res = await http.GetAsync(
                $"https://generativelanguage.googleapis.com/v1beta/models?key={Uri.EscapeDataString(key)}",
                cancellationToken);
            var raw = await res.Content.ReadAsStringAsync(cancellationToken);
            if (!res.IsSuccessStatusCode)
                return new CatalogProvider("gemini", [], $"HTTP {(int)res.StatusCode}");
            using var doc = JsonDocument.Parse(raw);
            var ids = new List<CatalogModel>();
            if (doc.RootElement.TryGetProperty("models", out var models))
            {
                foreach (var m in models.EnumerateArray())
                {
                    var name = m.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (string.IsNullOrWhiteSpace(name))
                        continue;
                    var id = name.StartsWith("models/", StringComparison.Ordinal) ? name["models/".Length..] : name;
                    if (IsChatModel(id))
                        ids.Add(new CatalogModel(id));
                }
            }

            return new CatalogProvider("gemini", ids, null);
        }
        catch (Exception ex)
        {
            return new CatalogProvider("gemini", [], ex.Message);
        }
    }

    private static List<CatalogModel> ReadOpenAiIds(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        var ids = new List<CatalogModel>();
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return ids;
        foreach (var m in data.EnumerateArray())
        {
            var id = m.TryGetProperty("id", out var p) ? p.GetString() : null;
            if (!string.IsNullOrWhiteSpace(id) && IsChatModel(id))
                ids.Add(new CatalogModel(id));
        }

        return ids;
    }
}

public sealed record RoleEffective(string Role, string Provider, string Model, string Source);
