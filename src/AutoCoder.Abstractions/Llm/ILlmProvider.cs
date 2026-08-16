namespace AutoCoder.Abstractions;

public sealed class LlmMessage
{
    public required string Role { get; init; }
    public required string Content { get; init; }
}

public sealed class LlmRequest
{
    public required string ModelRole { get; init; } // cheap: summarize|scout|comment|coding · costly: planning|thinking|decision
    public required IReadOnlyList<LlmMessage> Messages { get; init; }
    public int? MaxTokens { get; init; }
}

public sealed class LlmResponse
{
    public required string Content { get; init; }
    public int PromptTokens { get; init; }
    public int CompletionTokens { get; init; }
    public decimal EstimatedUsdCost { get; init; }
}

/// <summary>Pluggable model backend (Azure OpenAI / OpenAI / Anthropic).</summary>
public interface ILlmProvider
{
    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default);
}
