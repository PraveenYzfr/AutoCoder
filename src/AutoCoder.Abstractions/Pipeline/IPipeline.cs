namespace AutoCoder.Abstractions;

public sealed class ImplementationPlan
{
    public required string Summary { get; init; }
    public IReadOnlyList<string> Steps { get; init; } = [];
    public IReadOnlyList<string> FilesLikelyTouched { get; init; } = [];
    public IReadOnlyList<string> Risks { get; init; } = [];
    public IReadOnlyList<string> TestPlan { get; init; } = [];
    public string RawMarkdown { get; init; } = "";
}

public enum ApprovalDecision
{
    Approved,
    Rejected,
    NeedsClarification
}

public sealed class ApprovalResult
{
    public required ApprovalDecision Decision { get; init; }
    public string? Notes { get; init; }
}

public interface IApprovalGate
{
    Task<ApprovalResult> RequestApprovalAsync(ImplementationPlan plan, CancellationToken cancellationToken = default);
}

/// <summary>Shared bag for pipeline steps.</summary>
public sealed class PipelineContext
{
    public required string RunId { get; init; }
    public required string PipelineName { get; init; }
    public Ticket? Ticket { get; set; }
    public string? ProjectName { get; set; }
    public string? RepoUrl { get; set; }
    public string BaseBranch { get; set; } = "main";
    /// <summary>Configured Jira site, e.g. https://acme.atlassian.net</summary>
    public string? JiraBaseUrl { get; set; }
    public string? TicketBrowseUrl { get; set; }
    public string? WorkDirectory { get; set; }
    public string? BranchName { get; set; }
    public ImplementationPlan? Plan { get; set; }
    /// <summary>Normalized ticket text for planners (no LLM).</summary>
    public string? TicketBrief { get; set; }
    /// <summary>Cheap-model repo briefing given to Claude for planning.</summary>
    public string? RepoScout { get; set; }
    public ApprovalResult? Approval { get; set; }
    public PullRequestResult? PullRequest { get; set; }
    public string? FailureReason { get; set; }
    public bool DryRun { get; set; }
    public string ArtifactsDirectory { get; set; } = "runs";
    public string? DoneStatus { get; set; }
    public string? FailedStatus { get; set; }
    public string? RunningStatus { get; set; }
    public int ProductFilesChanged { get; set; }
    public bool BuildSucceeded { get; set; }
    public bool TestsSucceeded { get; set; }
    public bool TestsSkipped { get; set; }
    public string? TestSkipReason { get; set; }
    public string? AgentSummary { get; set; }
    public List<string> ChangedRelativePaths { get; } = [];
    public RunSpend Spend { get; } = new();
    public Dictionary<string, object?> Items { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Accumulated LLM/tool usage for one pipeline run.</summary>
public sealed class RunSpend
{
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int TotalTokens => PromptTokens + CompletionTokens;
    public int ToolCalls { get; set; }
    public decimal EstimatedUsd { get; set; }
}

public interface IPipelineStep
{
    string Name { get; }
    Task ExecuteAsync(PipelineContext context, CancellationToken cancellationToken = default);
}

/// <summary>Named ordered pipeline (e.g. fix-bug).</summary>
public interface IPipeline
{
    string Name { get; }
    IReadOnlyList<IPipelineStep> Steps { get; }
}
