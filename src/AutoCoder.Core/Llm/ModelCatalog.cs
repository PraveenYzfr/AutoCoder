using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using AutoCoder.Abstractions.Config;

namespace AutoCoder.Core.Llm;

public sealed record CatalogModel(string Id);

public sealed record CatalogProvider(string Name, IReadOnlyList<CatalogModel> Models, string? Error);

/// <summary>
/// Live provider /models inventories, filtered to AutoCoder-usable chat/coding LLMs.
/// Non-chat modalities (audio, image, video, embeddings, guards, TTS) are dropped.
/// Dated snapshot IDs are dropped when a stable alias family is preferred.
/// Inventory baseline: September 2026 official docs + live /models probes.
/// </summary>
public static partial class ModelCatalog
{
    // Global modality / specialty junk that never belongs in an AutoCoder role picker.
    private static readonly string[] Unusable =
    [
        "whisper", "tts", "orpheus", "prompt-guard", "safeguard",
        "embed", "embedding", "moderation", "transcri", "diarize",
        "speech-to", "text-to-speech", "realtime", "audio",
        "dall-e", "dalle", "gpt-image", "chatgpt-image", "sora",
        "image", "imagen", "veo-", "lyria", "nano-banana",
        "rerank", "classifier", "vision-exp", "computer-use",
        "deep-research", "robotics", "antigravity", "aqa",
        "babbage", "davinci", "instruct", "search-preview", "search-api",
        "live-translate", "native-audio", "-live-", "-live"
    ];

    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);
    private static readonly object Gate = new();
    private static List<CatalogProvider>? _cache;
    private static DateTime _cacheUtc;

    [GeneratedRegex(@"^gpt-5\.(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex OpenAiGpt5Minor();

    [GeneratedRegex(@"-\d{4}-\d{2}-\d{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex OpenAiDatedSnapshot();

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

    /// <summary>True when <paramref name="id"/> is a text chat/coding model suitable for AutoCoder roles.</summary>
    internal static bool IsChatModel(string id) => IsChatModel(id, provider: null);

    internal static bool IsChatModel(string id, string? provider)
    {
        if (string.IsNullOrWhiteSpace(id))
            return false;

        var n = id.ToLowerInvariant();
        if (Unusable.Any(bad => n.Contains(bad, StringComparison.Ordinal)))
            return false;

        return (provider?.Trim().ToLowerInvariant()) switch
        {
            "deepseek" => IsDeepSeekChat(n),
            "groq" => IsGroqChat(n),
            "openai" => IsOpenAiChat(n),
            "anthropic" => IsAnthropicChat(n),
            "gemini" => IsGeminiChat(n),
            _ => true
        };
    }

    /// <summary>
    /// DeepSeek (Sept 2026): deepseek-v4-flash, deepseek-v4-pro.
    /// Vision-exp is already denied via Unusable.
    /// </summary>
    private static bool IsDeepSeekChat(string n) =>
        n is "deepseek-v4-flash" or "deepseek-v4-pro"
        || (n.StartsWith("deepseek-v4-", StringComparison.Ordinal)
            && !n.Contains("vision", StringComparison.Ordinal));

    /// <summary>
    /// Groq (Sept 2026): gpt-oss, Qwen 3.x, MiniMax, Llama when present.
    /// Drops Compound systems, Whisper, Orpheus, prompt-guard, safeguard.
    /// </summary>
    private static bool IsGroqChat(string n)
    {
        if (n.StartsWith("groq/compound", StringComparison.Ordinal))
            return false;
        if (n.Contains("gpt-oss", StringComparison.Ordinal))
            return true;
        if (n.StartsWith("qwen/", StringComparison.Ordinal) || n.Contains("qwen", StringComparison.Ordinal))
            return true;
        if (n.Contains("minimax", StringComparison.Ordinal))
            return true;
        if (n.Contains("llama", StringComparison.Ordinal))
            return true;
        if (n.StartsWith("allam-", StringComparison.Ordinal))
            return true;
        return false;
    }

    /// <summary>
    /// OpenAI (Sept 2026): GPT-5.6 Sol/Terra/Luna + GPT-5.3/5.4/5.5 chat/coding aliases.
    /// Drops modality models, ChatGPT-only IDs, dated snapshots, GPT-4.x, and older GPT-5.0–5.2.
    /// </summary>
    private static bool IsOpenAiChat(string n)
    {
        if (OpenAiDatedSnapshot().IsMatch(n))
            return false;
        if (n is "chat-latest" || n.EndsWith("-chat-latest", StringComparison.Ordinal) || n.StartsWith("chatgpt-", StringComparison.Ordinal))
            return false;

        var minor = OpenAiGpt5Minor().Match(n);
        if (!minor.Success)
            return false;
        return int.TryParse(minor.Groups[1].Value, out var ver) && ver >= 3;
    }

    /// <summary>
    /// Anthropic (Sept 2026): Claude Fable/Opus/Sonnet/Haiku chat models.
    /// Prefer undated aliases; keep dated IDs only when that is what /models returns for a family.
    /// </summary>
    private static bool IsAnthropicChat(string n)
    {
        if (!n.StartsWith("claude-", StringComparison.Ordinal))
            return false;
        // Mythos is limited-access; omit from the general picker.
        if (n.Contains("mythos", StringComparison.Ordinal))
            return false;
        return true;
    }

    /// <summary>
    /// Gemini (Sept 2026): text Flash/Pro/Lite + Gemma instruct.
    /// Image / TTS / Live / music / video / research agents are excluded.
    /// </summary>
    private static bool IsGeminiChat(string n)
    {
        if (n.StartsWith("gemma-", StringComparison.Ordinal) && n.Contains("-it", StringComparison.Ordinal))
            return true;
        if (n is "gemini-flash-latest" or "gemini-flash-lite-latest" or "gemini-pro-latest")
            return true;
        if (!n.StartsWith("gemini-", StringComparison.Ordinal))
            return false;
        // Omni / custom-tool / high-res experimental variants are not general coding LLMs.
        if (n.Contains("omni", StringComparison.Ordinal) || n.Contains("customtools", StringComparison.Ordinal)
            || n.Contains("high-res", StringComparison.Ordinal))
            return false;
        // Keep numbered Gemini text models: gemini-3.8-flash, gemini-2.5-pro, gemini-3-flash-preview, …
        return n.Contains("flash", StringComparison.Ordinal)
               || n.Contains("pro", StringComparison.Ordinal);
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
            return new CatalogProvider(name, ReadOpenAiIds(raw, name), null);
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
            return new CatalogProvider("anthropic", ReadOpenAiIds(raw, "anthropic"), null);
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
                $"https://generativelanguage.googleapis.com/v1beta/models?key={Uri.EscapeDataString(key)}&pageSize=200",
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
                    if (!SupportsGenerateContent(m))
                        continue;
                    if (IsChatModel(id, "gemini"))
                        ids.Add(new CatalogModel(id));
                }
            }

            return new CatalogProvider("gemini", SortIds(ids), null);
        }
        catch (Exception ex)
        {
            return new CatalogProvider("gemini", [], ex.Message);
        }
    }

    private static bool SupportsGenerateContent(JsonElement model)
    {
        if (!model.TryGetProperty("supportedGenerationMethods", out var methods)
            || methods.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var method in methods.EnumerateArray())
        {
            if (method.GetString()?.Equals("generateContent", StringComparison.OrdinalIgnoreCase) == true)
                return true;
        }

        return false;
    }

    private static List<CatalogModel> ReadOpenAiIds(string raw, string provider)
    {
        using var doc = JsonDocument.Parse(raw);
        var ids = new List<CatalogModel>();
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return ids;
        foreach (var m in data.EnumerateArray())
        {
            var id = m.TryGetProperty("id", out var p) ? p.GetString() : null;
            if (!string.IsNullOrWhiteSpace(id) && IsChatModel(id, provider))
                ids.Add(new CatalogModel(id));
        }

        return SortIds(ids);
    }

    private static List<CatalogModel> SortIds(List<CatalogModel> ids) =>
        ids.OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase).ToList();
}

public sealed record RoleEffective(string Role, string Provider, string Model, string Source);
