using AutoCoder.Abstractions;
using AutoCoder.Abstractions.Config;

namespace AutoCoder.Core.Pipelines;

public sealed class FixBugPipeline : IPipeline
{
    public string Name => "fix-bug";

    public IReadOnlyList<IPipelineStep> Steps { get; }

    public FixBugPipeline(
        AutoCoderOptions options,
        ITicketSource ticketSource,
        ILlmProvider llm,
        IApprovalGate approvalGate,
        ISandboxRunner sandbox,
        IRepoHost repoHost)
    {
        Steps =
        [
            new FetchTicketStep(ticketSource),
            new ResolveProjectStep(options),
            new GeneratePlanStep(llm),
            new ApprovalGateStep(approvalGate),
            new ProvisionSandboxStep(sandbox, repoHost),
            new AgenticImplementStep(options),
            new BuildStep(sandbox),
            new TestStep(sandbox),
            new SecretScanStep(),
            new CommitAndOpenPrStep(repoHost),
            new WritebackTicketStep(ticketSource, llm),
            new PersistRunResultStep()
        ];
    }
}
