using AutoCoder.Abstractions;

namespace AutoCoder.Core.DryRun;

/// <summary>Dry-run auto-approves with a clear banner so demos work offline.</summary>
public sealed class DryRunApprovalGate : IApprovalGate
{
    public Task<ApprovalResult> RequestApprovalAsync(ImplementationPlan plan, CancellationToken cancellationToken = default)
    {
        Console.WriteLine();
        Console.WriteLine("════════════════════════════════════════════════════════");
        Console.WriteLine(" HUMAN APPROVAL GATE (dry-run: auto-approved)");
        Console.WriteLine(" In real runs this blocks until a human ratifies the plan.");
        Console.WriteLine("════════════════════════════════════════════════════════");
        Console.WriteLine(plan.RawMarkdown);
        Console.WriteLine("════════════════════════════════════════════════════════");

        return Task.FromResult(new ApprovalResult
        {
            Decision = ApprovalDecision.Approved,
            Notes = "dry-run auto-approve"
        });
    }
}
