using AutoCoder.Abstractions;
using AutoCoder.Core.Llm;

namespace AutoCoder.Core.DryRun;

/// <summary>Heuristic planner — no network. Replace with real ILlmProvider in Phase 1.</summary>
public sealed class HeuristicLlmProvider : ILlmProvider
{
    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        var user = request.Messages.LastOrDefault(m => m.Role == "user")?.Content ?? "";
        var content = $"""
            ## Plan (heuristic dry-run)

            Based on the ticket text (no model call):

            {Truncate(user, 800)}

            ### Proposed approach
            1. Locate the auth/login validation path mentioned in the ticket.
            2. Fix encoding/parsing so '+' in passwords is handled correctly.
            3. Add a regression test for passwords containing '+'.
            4. Run the project test suite.

            ### Out of scope
            - Unrelated refactors
            - Auto-merge
            """;

        return Task.FromResult(LlmUsage.Complete("heuristic", "none", content, EstimateTokens(user), EstimateTokens(content)));
    }

    private static int EstimateTokens(string text) => Math.Max(1, text.Length / 4);

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";
}
