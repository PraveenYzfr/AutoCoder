using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AutoCoder.Core.Llm;
using AutoCoder.Core.Resilience;

namespace AutoCoder.Core.Agent;

/// <summary>Anthropic Messages API with tool use for the coding loop (not OpenAI/Gemini wire format).</summary>
internal sealed class AnthropicToolClient
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;
    private readonly string _model;

    public AnthropicToolClient(HttpClient http, string apiKey, string model)
    {
        _http = http;
        _model = model;
        _http.DefaultRequestHeaders.TryAddWithoutValidation("x-api-key", apiKey);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<AnthropicTurn> GenerateAsync(
        string system,
        List<object> messages,
        CancellationToken cancellationToken)
    {
        LlmDailyBudget.Consume();

        var payload = new Dictionary<string, object?>
        {
            ["model"] = _model,
            ["max_tokens"] = 8192,
            ["system"] = system,
            ["messages"] = messages,
            ["tools"] = ToolDefs()
        };
        if (AnthropicLlmProvider.AcceptsTemperature(_model))
            payload["temperature"] = 0.2;

        using var response = await TransientRetry.SendAsync(
            "agent.anthropic",
            ct => _http.PostAsJsonAsync("https://api.anthropic.com/v1/messages", payload, Json, ct),
            cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Anthropic agent error {(int)response.StatusCode}: {raw[..Math.Min(800, raw.Length)]}");

        LlmUsage.AddAnthropicUsage(_model, raw);

        using var doc = JsonDocument.Parse(raw);
        var parts = new List<AnthropicPart>();
        var rawBlocks = new List<object>();
        if (!doc.RootElement.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"Anthropic returned no content: {raw[..Math.Min(800, raw.Length)]}");

        foreach (var block in content.EnumerateArray())
        {
            parts.Add(ParsePart(block));
            rawBlocks.Add(JsonSerializer.Deserialize<object>(block.GetRawText())!);
        }

        var stopReason = doc.RootElement.TryGetProperty("stop_reason", out var sr) ? sr.GetString() : null;

        return new AnthropicTurn { Parts = parts, RawContentBlocks = rawBlocks, StopReason = stopReason, Raw = raw };
    }

    private static AnthropicPart ParsePart(JsonElement block)
    {
        var type = block.TryGetProperty("type", out var t) ? t.GetString() : null;
        if (type == "tool_use")
        {
            return new AnthropicPart
            {
                ToolUseId = block.TryGetProperty("id", out var idEl) ? idEl.GetString() : null,
                FunctionName = block.TryGetProperty("name", out var n) ? n.GetString() : null,
                FunctionArgsJson = block.TryGetProperty("input", out var input) ? input.GetRawText() : "{}"
            };
        }

        return new AnthropicPart
        {
            Text = block.TryGetProperty("text", out var txt) ? txt.GetString() : null
        };
    }

    private static object[] ToolDefs() =>
    [
        Tool("list_files", "List files and folders relative to the repo root.",
            ("path", "Relative directory. Use empty or '.' for root.", false)),
        Tool("read_file", "Read a text file relative to the repo root.",
            ("path", "Relative file path", true)),
        Tool("write_file", "Create or overwrite a text file. Use this to implement the fix or feature.",
            ("path", "Relative file path", true),
            ("content", "Full file contents", true)),
        Tool("grep", "Search file contents for a string (case-insensitive).",
            ("pattern", "Text to find", true),
            ("path", "Relative file or directory to search", false)),
        Tool("finish", "Call when the code change is complete. Do not call until files are written.",
            ("summary", "What you changed and why", true))
    ];

    private static object Tool(string name, string description, params (string Name, string Description, bool Required)[] props)
    {
        var properties = new Dictionary<string, object>();
        var required = new List<string>();
        foreach (var p in props)
        {
            properties[p.Name] = new { type = "string", description = p.Description };
            if (p.Required)
                required.Add(p.Name);
        }

        return new
        {
            name,
            description,
            input_schema = new { type = "object", properties, required }
        };
    }
}

internal sealed class AnthropicTurn
{
    public List<AnthropicPart> Parts { get; init; } = [];
    public List<object> RawContentBlocks { get; init; } = [];
    public string? StopReason { get; init; }
    public string Raw { get; init; } = "";
}

internal sealed class AnthropicPart
{
    public string? Text { get; init; }
    public string? ToolUseId { get; init; }
    public string? FunctionName { get; init; }
    public string? FunctionArgsJson { get; init; }
    public bool IsFunction => !string.IsNullOrWhiteSpace(FunctionName);
}
