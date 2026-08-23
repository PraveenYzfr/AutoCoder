namespace AutoCoder.Core.Agent;

/// <summary>
/// Provider-agnostic coding-agent client. Wire format (OpenAI / Gemini / Anthropic) stays
/// inside each implementation; the turn driver only sees <see cref="CodingTurn"/>.
/// </summary>
internal interface ICodingToolClient
{
    string ProviderName { get; }
    string Model { get; }

    Task<CodingTurn> GenerateAsync(string system, List<object> history, CancellationToken cancellationToken);

    /// <summary>Append the model turn + tool results in this provider's conversation shape.</summary>
    void AppendToolRound(List<object> history, CodingTurn turn, IReadOnlyList<CodingToolExecution> executions);

    /// <summary>Append a text-only model turn and a user nudge (no tools called yet).</summary>
    void AppendNudge(List<object> history, CodingTurn turn, string nudge);
}

internal sealed class CodingTurn
{
    public IReadOnlyList<CodingPart> Parts { get; init; } = [];
    /// <summary>Opaque provider state needed to replay the assistant turn (e.g. Anthropic content blocks).</summary>
    public object? ProviderState { get; init; }
    public string Raw { get; init; } = "";

    public IReadOnlyList<CodingPart> FunctionCalls => Parts.Where(p => p.IsFunction).ToList();

    public string CombinedText =>
        string.Join("\n", Parts.Select(p => p.Text).Where(t => !string.IsNullOrWhiteSpace(t)));
}

internal sealed class CodingPart
{
    public string? Text { get; init; }
    public string? FunctionName { get; init; }
    public string? FunctionArgsJson { get; init; }
    public string? ToolCallId { get; init; }
    public bool IsFunction => !string.IsNullOrWhiteSpace(FunctionName);
}

internal sealed record CodingToolExecution(
    string Name,
    string ArgsJson,
    string ToolCallId,
    string Result);
