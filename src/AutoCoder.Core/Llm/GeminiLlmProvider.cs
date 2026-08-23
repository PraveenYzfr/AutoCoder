using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AutoCoder.Abstractions;
using AutoCoder.Core.Resilience;

namespace AutoCoder.Core.Llm;

/// <summary>Google Gemini via Generative Language API (API key).</summary>
public sealed class GeminiLlmProvider : ILlmProvider, IDisposable
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _defaultModel;
    private readonly Dictionary<string, string> _roleModels;
    private readonly bool _ownsHttp;

    public GeminiLlmProvider(
        string apiKey,
        string defaultModel = "gemini-2.0-flash",
        IReadOnlyDictionary<string, string>? roleModels = null,
        HttpClient? httpClient = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("Gemini API key is required.", nameof(apiKey));

        _apiKey = apiKey;
        _defaultModel = defaultModel;
        _roleModels = roleModels is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(roleModels, StringComparer.OrdinalIgnoreCase);
        _ownsHttp = httpClient is null;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
    }

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        LlmDailyBudget.Consume();
        var model = ResolveModel(request.ModelRole);
        var url =
            $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={Uri.EscapeDataString(_apiKey)}";

        var systemText = string.Join("\n\n",
            request.Messages.Where(m => m.Role.Equals("system", StringComparison.OrdinalIgnoreCase))
                .Select(m => m.Content));

        var contents = new List<GeminiContent>();
        foreach (var message in request.Messages.Where(m =>
                     !m.Role.Equals("system", StringComparison.OrdinalIgnoreCase)))
        {
            var role = message.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase)
                ? "model"
                : "user";
            contents.Add(new GeminiContent
            {
                Role = role,
                Parts = [new GeminiPart { Text = message.Content }]
            });
        }

        if (contents.Count == 0)
        {
            contents.Add(new GeminiContent
            {
                Role = "user",
                Parts = [new GeminiPart { Text = "Produce an implementation plan." }]
            });
        }

        var payload = new GeminiRequest
        {
            SystemInstruction = string.IsNullOrWhiteSpace(systemText)
                ? null
                : new GeminiContent { Parts = [new GeminiPart { Text = systemText }] },
            Contents = contents,
            GenerationConfig = new GeminiGenerationConfig
            {
                MaxOutputTokens = request.MaxTokens ?? 2048,
                Temperature = 0.2
            }
        };

        using var response = await TransientRetry.SendAsync(
            "llm.gemini",
            ct => _http.PostAsJsonAsync(url, payload, JsonOptions, ct),
            cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new LlmProviderException(
                "gemini",
                $"Gemini API error {(int)response.StatusCode}: {Truncate(raw, 800)}",
                (int)response.StatusCode);
        }

        var parsed = JsonSerializer.Deserialize<GeminiResponse>(raw, JsonOptions)
            ?? throw new LlmProviderException("gemini", "Empty Gemini response.", isEmptyContent: true);

        var text = parsed.Candidates?
            .SelectMany(c => c.Content?.Parts ?? [])
            .Select(p => p.Text)
            .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t))
            ?? throw new LlmProviderException("gemini", "Gemini returned no text candidates.", isEmptyContent: true);

        var promptTokens = parsed.UsageMetadata?.PromptTokenCount ?? EstimateTokens(systemText + string.Join("", request.Messages.Select(m => m.Content)));
        var completionTokens = parsed.UsageMetadata?.CandidatesTokenCount ?? EstimateTokens(text);

        return LlmUsage.Complete("gemini", model, text.Trim(), promptTokens, completionTokens);
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

    private static int EstimateTokens(string text) => Math.Max(1, text.Length / 4);

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed class GeminiRequest
    {
        public GeminiContent? SystemInstruction { get; set; }
        public List<GeminiContent> Contents { get; set; } = [];
        public GeminiGenerationConfig? GenerationConfig { get; set; }
    }

    private sealed class GeminiGenerationConfig
    {
        public int? MaxOutputTokens { get; set; }
        public double? Temperature { get; set; }
    }

    private sealed class GeminiContent
    {
        public string? Role { get; set; }
        public List<GeminiPart> Parts { get; set; } = [];
    }

    private sealed class GeminiPart
    {
        public string? Text { get; set; }
    }

    private sealed class GeminiResponse
    {
        public List<GeminiCandidate>? Candidates { get; set; }
        public GeminiUsage? UsageMetadata { get; set; }
    }

    private sealed class GeminiCandidate
    {
        public GeminiContent? Content { get; set; }
    }

    private sealed class GeminiUsage
    {
        public int PromptTokenCount { get; set; }
        public int CandidatesTokenCount { get; set; }
    }
}
