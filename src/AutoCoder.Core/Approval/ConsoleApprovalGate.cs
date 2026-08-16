using AutoCoder.Abstractions;

namespace AutoCoder.Core.Approval;

/// <summary>Interactive console approval. Use --yes / AUTOCODER_AUTO_APPROVE=true to skip prompt.</summary>
public sealed class ConsoleApprovalGate : IApprovalGate
{
    private readonly bool _autoApprove;

    public ConsoleApprovalGate(bool autoApprove = false) => _autoApprove = autoApprove;

    public Task<ApprovalResult> RequestApprovalAsync(ImplementationPlan plan, CancellationToken cancellationToken = default)
    {
        Console.WriteLine();
        Console.WriteLine("════════════════════════════════════════════════════════");
        Console.WriteLine(" HUMAN APPROVAL GATE — review plan before code/PR");
        Console.WriteLine("════════════════════════════════════════════════════════");
        Console.WriteLine(plan.RawMarkdown);
        Console.WriteLine("════════════════════════════════════════════════════════");

        if (_autoApprove
            || string.Equals(Environment.GetEnvironmentVariable("AUTOCODER_AUTO_APPROVE"), "true", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("[approval] Auto-approved (--yes / AUTOCODER_AUTO_APPROVE).");
            return Task.FromResult(new ApprovalResult
            {
                Decision = ApprovalDecision.Approved,
                Notes = "auto-approve"
            });
        }

        if (Console.IsInputRedirected)
        {
            throw new InvalidOperationException(
                "Plan approval required but stdin is not interactive. Re-run with --yes or set AUTOCODER_AUTO_APPROVE=true.");
        }

        Console.Write("Approve plan? [y/N]: ");
        var answer = Console.ReadLine()?.Trim();
        if (string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase)
            || string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(new ApprovalResult
            {
                Decision = ApprovalDecision.Approved,
                Notes = "console-yes"
            });
        }

        return Task.FromResult(new ApprovalResult
        {
            Decision = ApprovalDecision.Rejected,
            Notes = answer ?? "no"
        });
    }
}
