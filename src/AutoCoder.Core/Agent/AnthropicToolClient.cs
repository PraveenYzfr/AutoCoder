using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AutoCoder.Core.Llm;
using AutoCoder.Core.Resilience;

namespace AutoCoder.Core.Agent;

/// <summary>Anthropic Messages API with tool use for the coding loop (not OpenAI/Gemini wire format).</summary>
internal sealed class AnthropicToolClient : ICodingToolClient
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

    public string ProviderName => "anthropic";
    public string Model => _model;

    public async Task<CodingTurn> GenerateAsync(string system, List<object> history, CancellationToken cancellationToken)
    {
        LlmDailyBudget.Consume();

        var payload = new Dictionary<string, object?>
        {
            ["model"] = _model,
            ["max_tokens"] = 8192,
            ["system"] = system,
            ["messages"] = history,
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
        var parts = new List<CodingPart>();
        var rawBlocks = new List<object>();
        if (!doc.RootElement.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"Anthropic returned no content: {raw[..Math.Min(800, raw.Length)]}");

        foreach (var block in content.EnumerateArray())
        {
            parts.Add(ParsePart(block));
            rawBlocks.Add(JsonSerializer.Deserialize<object>(block.GetRawText())!);
        }

        return new CodingTurn { Parts = parts, ProviderState = rawBlocks, Raw = raw };
    }

    public void AppendToolRound(List<object> history, CodingTurn turn, IReadOnlyList<CodingToolExecution> executions)
    {
        var assistantContent = turn.ProviderState ?? turn.CombinedText;
        history.Add(new { role = "assistant", content = assistantContent });
        history.Add(new
        {
            role = "user",
            content = executions.Select(e => (object)new
            {
                type = "tool_result",
                tool_use_id = e.ToolCallId,
                content = e.Result
            }).ToList()
        });
    }

    public void AppendNudge(List<object> history, CodingTurn turn, string nudge)
    {
        var assistantContent = turn.ProviderState ?? turn.CombinedText;
        history.Add(new { role = "assistant", content = assistantContent });
        history.Add(new { role = "user", content = nudge });
    }

    private static CodingPart ParsePart(JsonElement block)
    {
        var type = block.TryGetProperty("type", out var t) ? t.GetString() : null;
        if (type == "tool_use")
        {
            return new CodingPart
            {
                ToolCallId = block.TryGetProperty("id", out var idEl) ? idEl.GetString() : null,
                FunctionName = block.TryGetProperty("name", out var n) ? n.GetString() : null,
                FunctionArgsJson = block.TryGetProperty("input", out var input) ? input.GetRawText() : "{}"
            };
        }

        return new CodingPart
        {
            Text = block.TryGetProperty("text", out var txt) ? txt.GetString() : null
        };
    }

    private static object[] ToolDefs() =>
        CodingToolCatalog.Tools.Select(t =>
        {
            var properties = new Dictionary<string, object>();
            var required = new List<string>();
            foreach (var p in t.Parameters)
            {
                properties[p.Name] = new { type = "string", description = p.Description };
                if (p.Required)
                    required.Add(p.Name);
            }

            return (object)new
            {
                name = t.Name,
                description = t.Description,
                input_schema = new { type = "object", properties, required }
            };
        }).ToArray();
}
