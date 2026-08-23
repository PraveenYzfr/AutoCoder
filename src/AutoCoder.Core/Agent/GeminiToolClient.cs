using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AutoCoder.Core.Llm;
using AutoCoder.Core.Resilience;

namespace AutoCoder.Core.Agent;

/// <summary>Gemini generateContent with function-calling for the coding loop.</summary>
internal sealed class GeminiToolClient : ICodingToolClient
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

    public string ProviderName => "gemini";
    public string Model => _model;

    public async Task<CodingTurn> GenerateAsync(string system, List<object> history, CancellationToken cancellationToken)
    {
        LlmDailyBudget.Consume();
        var url =
            $"https://generativelanguage.googleapis.com/v1beta/models/{_model}:generateContent?key={Uri.EscapeDataString(_apiKey)}";

        var payload = new Dictionary<string, object?>
        {
            ["systemInstruction"] = new { parts = new[] { new { text = system } } },
            ["contents"] = history,
            ["tools"] = new[] { new { functionDeclarations = ToolDefs() } },
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
        var parts = new List<CodingPart>();
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

        return new CodingTurn { Parts = parts, Raw = raw };
    }

    public void AppendToolRound(List<object> history, CodingTurn turn, IReadOnlyList<CodingToolExecution> executions)
    {
        var modelParts = new List<object>();
        var fnResponses = new List<object>();
        foreach (var e in executions)
        {
            var argsDict = ParseArgsObject(e.ArgsJson);
            modelParts.Add(new { functionCall = new { name = e.Name, args = argsDict } });
            fnResponses.Add(new
            {
                functionResponse = new
                {
                    name = e.Name,
                    response = new Dictionary<string, string> { ["result"] = e.Result }
                }
            });
        }

        history.Add(new { role = "model", parts = modelParts });
        history.Add(new { role = "user", parts = fnResponses });
    }

    public void AppendNudge(List<object> history, CodingTurn turn, string nudge)
    {
        history.Add(new { role = "model", parts = new object[] { new { text = turn.CombinedText } } });
        history.Add(new { role = "user", parts = new object[] { new { text = nudge } } });
    }

    private static CodingPart ParsePart(JsonElement p)
    {
        if (p.TryGetProperty("functionCall", out var fc))
        {
            return new CodingPart
            {
                FunctionName = fc.GetProperty("name").GetString(),
                FunctionArgsJson = fc.TryGetProperty("args", out var args) ? args.GetRawText() : "{}"
            };
        }

        return new CodingPart
        {
            Text = p.TryGetProperty("text", out var t) ? t.GetString() : null
        };
    }

    private static Dictionary<string, object?> ParseArgsObject(string argsJson)
    {
        var argsDict = new Dictionary<string, object?>();
        try
        {
            using var argsDoc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
            foreach (var prop in argsDoc.RootElement.EnumerateObject())
                argsDict[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                    ? prop.Value.GetString()
                    : prop.Value.GetRawText();
        }
        catch
        {
            // empty args
        }

        return argsDict;
    }

    private static object[] ToolDefs() =>
        CodingToolCatalog.Tools.Select(t =>
        {
            var props = new Dictionary<string, object>();
            var required = new List<string>();
            foreach (var p in t.Parameters)
            {
                props[p.Name] = new { type = "string", description = p.Description };
                if (p.Required)
                    required.Add(p.Name);
            }

            return (object)new
            {
                name = t.Name,
                description = t.Description,
                parameters = new { type = "object", properties = props, required }
            };
        }).ToArray();
}
