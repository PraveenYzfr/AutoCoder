namespace AutoCoder.Core.Llm;

/// <summary>
/// Conservative USD per 1M tokens. Used to enforce max_usd_per_run; not an invoice.
/// </summary>
public static class LlmPricing
{
    public static decimal Estimate(string provider, string model, int promptTokens, int completionTokens)
    {
        var (input, output) = Rates(provider, model);
        return (promptTokens / 1_000_000m) * input + (completionTokens / 1_000_000m) * output;
    }

    private static (decimal Input, decimal Output) Rates(string provider, string model)
    {
        var p = (provider ?? "").Trim().ToLowerInvariant();
        var m = (model ?? "").Trim().ToLowerInvariant();

        if (p is "deepseek" || m.Contains("deepseek"))
            return m.Contains("pro") || m.Contains("reasoner") ? (0.55m, 2.19m) : (0.14m, 0.28m);

        if (p is "groq" || m.Contains("llama") || m.Contains("groq"))
            return (0.05m, 0.08m);

        if (p is "openai" || m.StartsWith("gpt-"))
            return m.Contains("4o-mini") || m.Contains("mini") ? (0.15m, 0.60m) : (2.50m, 10.00m);

        if (p is "anthropic" or "claude" || m.Contains("claude"))
            return m.Contains("haiku") ? (0.80m, 4.00m) : (3.00m, 15.00m);

        if (p is "gemini" or "google" || m.Contains("gemini"))
            return m.Contains("pro") || m.Contains("ultra") ? (1.25m, 10.00m) : (0.15m, 0.60m);

        if (p is "heuristic")
            return (0m, 0m);

        return (5.00m, 15.00m);
    }
}
