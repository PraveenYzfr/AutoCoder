using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AutoCoder.Abstractions;
using AutoCoder.Core.Logging;
using AutoCoder.Core.Resilience;
using Microsoft.Extensions.Logging;

namespace AutoCoder.Core.Llm;

/// <summary>
/// OpenAI chat-completions wire format. Used by the named <c>deepseek</c> provider
/// (base https://api.deepseek.com/v1). Not a Gemini client — different request shape.
/// DeepSeek has JSON mode but no server-side schema binding.
/// </summary>
public sealed class OpenAiCompatibleLlmProvider : ILlmProvider, IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _baseUrl;
    private readonly string _defaultModel;
    private readonly string _providerName;
    private readonly Dictionary<string, string> _roleModels;

    public OpenAiCompatibleLlmProvider(
        string apiKey,
        string baseUrl,
        string defaultModel,
        string providerName,
        IReadOnlyDictionary<string, string>? roleModels = null,
        HttpClient? httpClient = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("API key is required.", nameof(apiKey));

        _baseUrl = baseUrl.TrimEnd('/');
        _defaultModel = defaultModel;
        _providerName = providerName;
        _roleModels = roleModels is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(roleModels, StringComparer.OrdinalIgnoreCase);
        _ownsHttp = httpClient is null;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        LlmDailyBudget.Consume();
        var model = ResolveModel(request.ModelRole);
        var messages = request.Messages.Select(m => new
        {
            role = m.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase) ? "assistant" : m.Role,
            content = m.Content
        }).ToList();

        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = messages,
            ["max_tokens"] = request.MaxTokens ?? 2048
        };
        if (_providerName.Equals("deepseek", StringComparison.OrdinalIgnoreCase))
        {
            model = DeepSeekModels.Sanitize(model);
            payload["model"] = model;
            var think = DeepSeekModels.ThinkingRequested()
                        && !RoutedLlmProvider.IsCheap(request.ModelRole);
            DeepSeekModels.ApplyThinking(payload, think);
            if (think && request.MaxTokens is null)
                payload["max_tokens"] = 8192;
        }
        else
        {
            payload["temperature"] = 0.2;
        }

        var url = $"{_baseUrl}/chat/completions";
        using var response = await TransientRetry.SendAsync(
            $"llm.{_providerName}",
            ct => _http.PostAsJsonAsync(url, payload, Json, ct),
            cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var err = $"{_providerName} error {(int)response.StatusCode}: {Truncate(raw, 800)}";
            if (LlmUsage.Current?.Context is { } ctx)
            {
                RunLog.Event(
                    "llm.error",
                    ctx,
                    LogLevel.Error,
                    fields:
                    [
                        ("role", request.ModelRole),
                        ("provider", _providerName),
                        ("model", model),
                        ("error", err)
                    ]);
            }
            throw new InvalidOperationException(err);
        }

        using var doc = JsonDocument.Parse(raw);
        var text = "";
        if (doc.RootElement.TryGetProperty("choices", out var choices)
            && choices.GetArrayLength() > 0)
        {
            var msg = choices[0].GetProperty("message");
            text = DeepSeekModels.ReadMessageText(msg);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException(
                $"{_providerName} returned empty content (reasoning models can spend max_tokens on thinking). "
                + "Raise MaxTokens or set AUTOCODER_DEEPSEEK_THINKING=false.");
        }

        var prompt = 0;
        var completion = 0;
        if (doc.RootElement.TryGetProperty("usage", out var usage))
        {
            prompt = usage.TryGetProperty("prompt_tokens", out var p) ? p.GetInt32() : 0;
            completion = usage.TryGetProperty("completion_tokens", out var t) ? t.GetInt32() : 0;
        }

        return LlmUsage.Complete(_providerName, model, text.Trim(), prompt, completion);
    }

    private string ResolveModel(string modelRole)
    {
        var model = _roleModels.TryGetValue(modelRole, out var named) && !string.IsNullOrWhiteSpace(named)
            ? named
            : _defaultModel;
        if (_providerName.Equals("deepseek", StringComparison.OrdinalIgnoreCase))
            return DeepSeekModels.Sanitize(model);
        if (_providerName.Equals("groq", StringComparison.OrdinalIgnoreCase))
            return GroqModels.Sanitize(model);
        return model;
    }

    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";
}
