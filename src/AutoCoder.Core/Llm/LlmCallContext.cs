namespace AutoCoder.Core.Llm;

/// <summary>Ambient role/tier for the in-flight LLM call so usage can log which model did what.</summary>
internal static class LlmCallContext
{
    private static readonly AsyncLocal<string?> Role = new();
    private static readonly AsyncLocal<string?> Tier = new();

    public static string? CurrentRole
    {
        get => Role.Value;
        set => Role.Value = value;
    }

    public static string? CurrentTier
    {
        get => Tier.Value;
        set => Tier.Value = value;
    }
}
