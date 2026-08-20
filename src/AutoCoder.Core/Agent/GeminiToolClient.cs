using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AutoCoder.Core.Llm;
using AutoCoder.Core.Resilience;

namespace AutoCoder.Core.Agent;

/// <summary>Gemini generateContent with function-calling for the coding loop.</summary>
internal sealed class GeminiToolClient
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _model;

    public GeminiToolClient(HttpClient http, string apiKey, string model)
    {
        _http = http;
        _apiKey = apiKey;
        _model = model;
    }

    public async Task<GeminiTurn> GenerateAsync(
        string system,
        List<object> contents,
        CancellationToken cancellationToken)
    {
        LlmDailyBudget.Consume();
        var url =
            $"https://generativelanguage.googleapis.com/v1beta/models/{_model}:generateContent?key={Uri.EscapeDataString(_apiKey)}";

        var payload = new Dictionary<string, object?>
        {
            ["systemInstruction"] = new { parts = new[] { new { text = system } } },
            ["contents"] = contents,
            ["tools"] = new[]
            {
                new
                {
                    functionDeclarations = ToolDefs()
                }
            },
            ["generationConfig"] = new { temperature = 0.2, maxOutputTokens = 8192 }
        };

        using var response = await TransientRetry.SendAsync(
            "agent.gemini",
            ct => _http.PostAsJsonAsync(url, payload, Json, ct),
            cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Gemini agent error {(int)response.StatusCode}: {raw[..Math.Min(800, raw.Length)]}");

        LlmUsage.AddGeminiUsage(_model, raw);

        using var doc = JsonDocument.Parse(raw);
        var parts = new List<GeminiPart>();
        if (!doc.RootElement.TryGetProperty("candidates", out var cands)
            || cands.ValueKind != JsonValueKind.Array
            || cands.GetArrayLength() == 0)
        {
            throw new InvalidOperationException(
                $"Gemini returned no candidates: {raw[..Math.Min(800, raw.Length)]}");
        }

        var content = cands[0].GetProperty("content");
        if (content.TryGetProperty("parts", out var pEl) && pEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in pEl.EnumerateArray())
                parts.Add(ParsePart(p));
        }

        return new GeminiTurn { Parts = parts, Raw = raw };
    }

    private static GeminiPart ParsePart(JsonElement p)
    {
        if (p.TryGetProperty("functionCall", out var fc))
        {
            return new GeminiPart
            {
                FunctionName = fc.GetProperty("name").GetString(),
                FunctionArgs = fc.TryGetProperty("args", out var args) ? args.GetRawText() : "{}"
            };
        }

        return new GeminiPart
        {
            Text = p.TryGetProperty("text", out var t) ? t.GetString() : null
        };
    }

    private static object[] ToolDefs() =>
    [
        Fn("list_files", "List files and folders relative to the repo root.",
            Prop("path", "Relative directory. Use empty or '.' for root.")),
        Fn("read_file", "Read a text file relative to the repo root.",
            Prop("path", "Relative file path", required: true)),
        Fn("write_file", "Create or overwrite a text file. Use this to implement the fix or feature.",
            Prop("path", "Relative file path", required: true),
            Prop("content", "Full file contents", required: true)),
        Fn("grep", "Search file contents for a string (case-insensitive).",
            Prop("pattern", "Text to find", required: true),
            Prop("path", "Relative file or directory to search")),
        Fn("finish", "Call when the code change is complete. Do not call until files are written.",
            Prop("summary", "What you changed and why", required: true))
    ];

    private static object Fn(string name, string description, params object[] properties)
    {
        var props = new Dictionary<string, object>();
        var required = new List<string>();
        foreach (var p in properties)
        {
            var d = (Dictionary<string, object>)p;
            var n = (string)d["name"];
            props[n] = new { type = "string", description = d["description"] };
            if (d.TryGetValue("required", out var r) && r is true)
                required.Add(n);
        }

        return new
        {
            name,
            description,
            parameters = new
            {
                type = "object",
                properties = props,
                required
            }
        };
    }

    private static Dictionary<string, object> Prop(string name, string description, bool required = false) =>
        new()
        {
            ["name"] = name,
            ["description"] = description,
            ["required"] = required
        };
}

internal sealed class GeminiTurn
{
    public List<GeminiPart> Parts { get; init; } = [];
    public string Raw { get; init; } = "";
}

internal sealed class GeminiPart
{
    public string? Text { get; init; }
    public string? FunctionName { get; init; }
    public string? FunctionArgs { get; init; }
    public string? ToolCallId { get; init; }
    public bool IsFunction => !string.IsNullOrWhiteSpace(FunctionName);
}
