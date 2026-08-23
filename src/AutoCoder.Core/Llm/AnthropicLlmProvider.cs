using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AutoCoder.Abstractions;
using AutoCoder.Core.Resilience;

namespace AutoCoder.Core.Llm;

/// <summary>Anthropic Messages API (not OpenAI wire format).</summary>
public sealed class AnthropicLlmProvider : ILlmProvider, IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _defaultModel;
    private readonly Dictionary<string, string> _roleModels;

    public AnthropicLlmProvider(
        string apiKey,
        string defaultModel = "claude-sonnet-5",
        IReadOnlyDictionary<string, string>? roleModels = null,
        HttpClient? httpClient = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("Anthropic API key is required.", nameof(apiKey));

        _defaultModel = defaultModel;
        _roleModels = roleModels is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(roleModels, StringComparer.OrdinalIgnoreCase);
        _ownsHttp = httpClient is null;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("x-api-key", apiKey);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        LlmDailyBudget.Consume();
        var model = ResolveModel(request.ModelRole);
        var system = string.Join("\n\n",
            request.Messages.Where(m => m.Role.Equals("system", StringComparison.OrdinalIgnoreCase))
                .Select(m => m.Content));
        var messages = request.Messages
            .Where(m => !m.Role.Equals("system", StringComparison.OrdinalIgnoreCase))
            .Select(m => new
            {
                role = m.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase) ? "assistant" : "user",
                content = m.Content
            })
            .ToList();
        if (messages.Count == 0)
            messages.Add(new { role = "user", content = "Produce an implementation plan." });

        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["max_tokens"] = request.MaxTokens ?? 4096,
            ["messages"] = messages
        };
        // claude-sonnet-5 / Claude 4.x reject `temperature` (HTTP 400 deprecated).
        if (AcceptsTemperature(model))
            payload["temperature"] = 0.2;
        if (!string.IsNullOrWhiteSpace(system))
            payload["system"] = system;

        using var response = await TransientRetry.SendAsync(
            "llm.anthropic",
            ct => _http.PostAsJsonAsync("https://api.anthropic.com/v1/messages", payload, Json, ct),
            cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new LlmProviderException(
                "anthropic",
                $"Anthropic error {(int)response.StatusCode}: {Truncate(raw, 800)}",
                (int)response.StatusCode);
        }

        using var doc = JsonDocument.Parse(raw);
        var text = "";
        if (doc.RootElement.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var t) && t.GetString() == "text")
                    text += block.TryGetProperty("text", out var tx) ? tx.GetString() : "";
            }
        }

        if (string.IsNullOrWhiteSpace(text))
            throw new LlmProviderException("anthropic", "Anthropic returned no text.", isEmptyContent: true);

        var prompt = 0;
        var completion = 0;
        if (doc.RootElement.TryGetProperty("usage", out var usage))
        {
            prompt = usage.TryGetProperty("input_tokens", out var i) ? i.GetInt32() : 0;
            completion = usage.TryGetProperty("output_tokens", out var o) ? o.GetInt32() : 0;
        }

        return LlmUsage.Complete("anthropic", model, text.Trim(), prompt, completion);
    }

    internal static bool AcceptsTemperature(string model)
    {
        var m = (model ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(m))
            return true;
        return !(m.Contains("sonnet-5")
                 || m.Contains("sonnet-4")
                 || m.Contains("opus-4")
                 || m.Contains("haiku-4")
                 || m.Contains("claude-4")
                 || m.Contains("claude-sonnet-4"));
    }

    private string ResolveModel(string modelRole)
    {
        if (_roleModels.TryGetValue(modelRole, out var named) && !string.IsNullOrWhiteSpace(named))
            return named;
        return _defaultModel;
    }

    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";
}
