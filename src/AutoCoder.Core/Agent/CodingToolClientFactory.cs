using AutoCoder.Abstractions.Config;
using AutoCoder.Core.Llm;

namespace AutoCoder.Core.Agent;

internal static class CodingToolClientFactory
{
    public static ICodingToolClient Create(
        string providerType,
        string model,
        HttpClient http)
    {
        var type = (providerType ?? "").Trim().ToLowerInvariant();
        return type switch
        {
            "openai" => CreateOpenAi(http, "OPENAI_API_KEY", model, "https://api.openai.com/v1", "openai"),
            "groq" => CreateOpenAi(http, "GROQ_API_KEY", GroqModels.Sanitize(model), GroqModels.BaseUrl, "groq"),
            "gemini" or "google" => CreateGemini(http, model),
            "anthropic" or "claude" => CreateAnthropic(http, model),
            _ => CreateOpenAi(
                http,
                "DEEPSEEK_API_KEY",
                DeepSeekModels.Sanitize(model),
                "https://api.deepseek.com/v1",
                "deepseek")
        };
    }

    private static ICodingToolClient CreateOpenAi(
        HttpClient http, string apiKeyEnv, string model, string baseUrl, string providerName)
    {
        var key = Environment.GetEnvironmentVariable(apiKeyEnv)
                  ?? throw new InvalidOperationException($"{apiKeyEnv} is required for the {providerName} coding agent.");
        return new OpenAiToolClient(http, key, model, baseUrl, providerName);
    }

    private static ICodingToolClient CreateGemini(HttpClient http, string model)
    {
        var key = Environment.GetEnvironmentVariable("GEMINI_API_KEY")
                  ?? Environment.GetEnvironmentVariable("GOOGLE_API_KEY")
                  ?? throw new InvalidOperationException("GEMINI_API_KEY is required for the coding agent.");

        if (string.IsNullOrWhiteSpace(model)
            || model.Contains("deepseek", StringComparison.OrdinalIgnoreCase)
            || model.Contains("gpt-", StringComparison.OrdinalIgnoreCase)
            || model.Contains("claude", StringComparison.OrdinalIgnoreCase))
            model = "gemini-flash-lite-latest";

        return new GeminiToolClient(http, key, model);
    }

    private static ICodingToolClient CreateAnthropic(HttpClient http, string model)
    {
        var key = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
                  ?? throw new InvalidOperationException("ANTHROPIC_API_KEY is required for the coding agent.");

        if (string.IsNullOrWhiteSpace(model)
            || model.Contains("deepseek", StringComparison.OrdinalIgnoreCase)
            || model.Contains("gpt-", StringComparison.OrdinalIgnoreCase)
            || model.Contains("gemini", StringComparison.OrdinalIgnoreCase))
            model = "claude-sonnet-5";

        return new AnthropicToolClient(http, key, model);
    }
}
