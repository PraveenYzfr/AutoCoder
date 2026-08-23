using AutoCoder.Abstractions;
using AutoCoder.Core.Logging;
using AutoCoder.Core.Runs;

namespace AutoCoder.Core.Llm;

internal static class LlmUsage
{
    public static LlmResponse Complete(string provider, string model, string content, int promptTokens, int completionTokens)
    {
        var usd = Record(provider, model, promptTokens, completionTokens);
        return new LlmResponse
        {
            Content = content,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            EstimatedUsdCost = usd
        };
    }

    public static void Add(string provider, string model, int promptTokens, int completionTokens) =>
        Record(provider, model, promptTokens, completionTokens);

    private static decimal Record(string provider, string model, int promptTokens, int completionTokens)
    {
        var usd = LlmPricing.Estimate(provider, model, promptTokens, completionTokens);
        Current?.AddLlm(promptTokens, completionTokens, usd);
        var ctx = Current?.Context;
        if (ctx is not null)
        {
            RunLog.Event(
                "llm.call",
                ctx,
                fields:
                [
                    ("role", LlmCallContext.CurrentRole ?? "unknown"),
                    ("tier", LlmCallContext.CurrentTier ?? "unknown"),
                    ("provider", provider),
                    ("model", model),
                    ("prompt", promptTokens),
                    ("completion", completionTokens),
                    ("usd", usd)
                ]);
        }

        return usd;
    }

    public static void AddOpenAiUsage(string provider, string model, string rawJson)
    {
        if (!TryReadOpenAiUsage(rawJson, out var prompt, out var completion))
            return;
        Add(provider, model, prompt, completion);
    }

    public static void AddGeminiUsage(string model, string rawJson)
    {
        if (!TryReadGeminiUsage(rawJson, out var prompt, out var completion))
            return;
        Add("gemini", model, prompt, completion);
    }

    public static void AddAnthropicUsage(string model, string rawJson)
    {
        if (!TryReadAnthropicUsage(rawJson, out var prompt, out var completion))
            return;
        Add("anthropic", model, prompt, completion);
    }

    public static RunBudget? Current => RunBudget.Current;

    private static bool TryReadOpenAiUsage(string raw, out int prompt, out int completion)
    {
        prompt = 0;
        completion = 0;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("usage", out var usage))
                return false;
            prompt = usage.TryGetProperty("prompt_tokens", out var p) ? p.GetInt32() : 0;
            completion = usage.TryGetProperty("completion_tokens", out var c) ? c.GetInt32() : 0;
            return prompt > 0 || completion > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadGeminiUsage(string raw, out int prompt, out int completion)
    {
        prompt = 0;
        completion = 0;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("usageMetadata", out var usage)
                && !doc.RootElement.TryGetProperty("usage_metadata", out usage))
                return false;
            prompt = usage.TryGetProperty("promptTokenCount", out var p) ? p.GetInt32() : 0;
            completion = usage.TryGetProperty("candidatesTokenCount", out var c) ? c.GetInt32() : 0;
            return prompt > 0 || completion > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadAnthropicUsage(string raw, out int prompt, out int completion)
    {
        prompt = 0;
        completion = 0;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("usage", out var usage))
                return false;
            prompt = usage.TryGetProperty("input_tokens", out var p) ? p.GetInt32() : 0;
            completion = usage.TryGetProperty("output_tokens", out var c) ? c.GetInt32() : 0;
            return prompt > 0 || completion > 0;
        }
        catch
        {
            return false;
        }
    }
}
