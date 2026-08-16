using System.Text.Json;

namespace AutoCoder.Core.Llm;

/// <summary>
/// DeepSeek V4 IDs. Legacy <c>deepseek-chat</c> / <c>deepseek-reasoner</c> retired 2026-07-24.
/// Thinking is on by default and can return empty <c>content</c> if max_tokens is spent on reasoning.
/// </summary>
internal static class DeepSeekModels
{
    public const string Flash = "deepseek-v4-flash";
    public const string Pro = "deepseek-v4-pro";

    public static string Sanitize(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)
            || model.Equals("deepseek-chat", StringComparison.OrdinalIgnoreCase)
            || model.Equals("deepseek-reasoner", StringComparison.OrdinalIgnoreCase)
            || model.Contains("gemini", StringComparison.OrdinalIgnoreCase)
            || model.Contains("gpt-", StringComparison.OrdinalIgnoreCase)
            || model.Contains("claude", StringComparison.OrdinalIgnoreCase))
            return Flash;
        return model;
    }

    public static bool ThinkingRequested()
    {
        var raw = Environment.GetEnvironmentVariable("AUTOCODER_DEEPSEEK_THINKING");
        return bool.TryParse(raw, out var on) && on;
    }

    public static void ApplyThinking(Dictionary<string, object?> payload, bool enable) =>
        payload["thinking"] = new Dictionary<string, string> { ["type"] = enable ? "enabled" : "disabled" };

    public static string ReadMessageText(JsonElement message)
    {
        var text = message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString() ?? ""
            : "";
        if (!string.IsNullOrWhiteSpace(text))
            return text;
        if (message.TryGetProperty("reasoning_content", out var r) && r.ValueKind == JsonValueKind.String)
            return r.GetString() ?? "";
        return text;
    }
}
