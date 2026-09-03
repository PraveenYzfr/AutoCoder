namespace AutoCoder.Core.Llm;

/// <summary>
/// Groq (Q, not xAI Grok). OpenAI chat-completions at https://api.groq.com/openai/v1.
/// Distinct provider label so metrics are not filed under openai (hub §8e).
/// Latency-focused. Default is GPT-OSS 20B (Llama 3.1 8B is Enterprise-only as of Sept 2026).
/// List GET /openai/v1/models on the real key before changing pins.
/// </summary>
internal static class GroqModels
{
    public const string Fast = "openai/gpt-oss-20b";
    public const string Quality = "openai/gpt-oss-120b";
    public const string BaseUrl = "https://api.groq.com/openai/v1";

    public static string Sanitize(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return Fast;
        if (model.Contains("gemini", StringComparison.OrdinalIgnoreCase)
            || model.Contains("claude", StringComparison.OrdinalIgnoreCase)
            || model.Contains("deepseek", StringComparison.OrdinalIgnoreCase)
            || model.Equals("gpt-4o", StringComparison.OrdinalIgnoreCase)
            || model.StartsWith("gpt-4.", StringComparison.OrdinalIgnoreCase)
            || (model.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase)
                && !model.Contains("gpt-oss", StringComparison.OrdinalIgnoreCase)))
            return Fast;
        // Legacy pin that left the public Groq catalog.
        if (model.Equals("llama-3.1-8b-instant", StringComparison.OrdinalIgnoreCase)
            || model.Equals("llama-3.3-70b-versatile", StringComparison.OrdinalIgnoreCase))
            return Fast;
        return model;
    }
}
