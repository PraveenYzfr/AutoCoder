using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AutoCoder.Core.Llm;
using AutoCoder.Core.Resilience;

namespace AutoCoder.Core.Agent;

/// <summary>OpenAI-compatible chat completions + tools (DeepSeek, OpenAI, Groq).</summary>
internal sealed class OpenAiToolClient : ICodingToolClient
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;
    private readonly string _model;
    private readonly string _baseUrl;

    public OpenAiToolClient(HttpClient http, string apiKey, string model, string baseUrl, string providerName)
    {
        _http = http;
        _model = model;
        _baseUrl = baseUrl.TrimEnd('/');
        ProviderName = string.IsNullOrWhiteSpace(providerName) ? "openai" : providerName;
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public string ProviderName { get; }
    public string Model => _model;

    public async Task<CodingTurn> GenerateAsync(string system, List<object> history, CancellationToken cancellationToken)
    {
        LlmDailyBudget.Consume();
        var all = new List<object> { new { role = "system", content = system } };
        all.AddRange(history);

        var model = _model;
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = all,
            ["temperature"] = 0.2,
            ["max_tokens"] = 8192,
            ["tools"] = ToolDefs()
        };
        if (_baseUrl.Contains("deepseek", StringComparison.OrdinalIgnoreCase))
        {
            model = DeepSeekModels.Sanitize(model);
            payload["model"] = model;
            DeepSeekModels.ApplyThinking(payload, enable: false);
        }
        else if (_baseUrl.Contains("groq.com", StringComparison.OrdinalIgnoreCase))
        {
            model = GroqModels.Sanitize(model);
            payload["model"] = model;
        }

        var url = $"{_baseUrl}/chat/completions";
        using var response = await TransientRetry.SendAsync(
            $"agent.{ProviderName}",
            ct => _http.PostAsJsonAsync(url, payload, Json, ct),
            cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"{ProviderName} agent error {(int)response.StatusCode}: {raw[..Math.Min(800, raw.Length)]}");

        LlmUsage.AddOpenAiUsage(ProviderName, model, raw);

        using var doc = JsonDocument.Parse(raw);
        var parts = new List<CodingPart>();
        if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            throw new InvalidOperationException($"No choices: {raw[..Math.Min(800, raw.Length)]}");

        var message = choices[0].GetProperty("message");
        if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
        {
            var text = content.GetString();
            if (!string.IsNullOrWhiteSpace(text))
                parts.Add(new CodingPart { Text = text });
        }

        if (message.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
        {
            foreach (var call in calls.EnumerateArray())
            {
                var id = call.TryGetProperty("id", out var idEl) ? idEl.GetString() : "";
                var fn = call.TryGetProperty("function", out var f) ? f : default;
                parts.Add(new CodingPart
                {
                    ToolCallId = id,
                    FunctionName = fn.ValueKind == JsonValueKind.Object && fn.TryGetProperty("name", out var n)
                        ? n.GetString()
                        : null,
                    FunctionArgsJson = fn.ValueKind == JsonValueKind.Object && fn.TryGetProperty("arguments", out var a)
                        ? a.GetString()
                        : "{}"
                });
            }
        }

        return new CodingTurn { Parts = parts, Raw = raw };
    }

    public void AppendToolRound(List<object> history, CodingTurn turn, IReadOnlyList<CodingToolExecution> executions)
    {
        var text = turn.CombinedText;
        var toolCalls = executions.Select(e => (object)new
        {
            id = e.ToolCallId,
            type = "function",
            function = new { name = e.Name, arguments = e.ArgsJson }
        }).ToList();

        history.Add(new { role = "assistant", content = text, tool_calls = toolCalls });
        foreach (var e in executions)
            history.Add(new { role = "tool", tool_call_id = e.ToolCallId, content = e.Result });
    }

    public void AppendNudge(List<object> history, CodingTurn turn, string nudge)
    {
        history.Add(new { role = "assistant", content = turn.CombinedText });
        history.Add(new { role = "user", content = nudge });
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
                type = "function",
                function = new
                {
                    name = t.Name,
                    description = t.Description,
                    parameters = new { type = "object", properties, required }
                }
            };
        }).ToArray();
}
