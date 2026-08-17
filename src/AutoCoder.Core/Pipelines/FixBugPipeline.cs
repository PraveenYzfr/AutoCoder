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
            new ExtractTicketStep(),
            new ProvisionSandboxStep(sandbox, repoHost),
            new ScoutRepoStep(llm),
            new GeneratePlanStep(llm),
            new ApprovalGateStep(approvalGate),
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
