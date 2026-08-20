using System.Text;
using AutoCoder.Abstractions;
using AutoCoder.Abstractions.Config;
using AutoCoder.Core.Agent;
using AutoCoder.Core.Config;

namespace AutoCoder.Core.Pipelines;

public sealed class FetchTicketStep(ITicketSource ticketSource) : IPipelineStep
{
    public string Name => "FetchTicket";

    public async Task ExecuteAsync(PipelineContext context, CancellationToken cancellationToken = default)
    {
        var key = context.Items.TryGetValue("ticketKey", out var k) ? k?.ToString() ?? "from-file" : "from-file";
        context.Ticket = await ticketSource.FetchAsync(key, cancellationToken);

        if (string.IsNullOrWhiteSpace(context.TicketBrowseUrl) && !string.IsNullOrWhiteSpace(context.JiraBaseUrl))
            context.TicketBrowseUrl = ProjectCatalog.BrowseUrl(context.JiraBaseUrl, context.Ticket.Key);

        Console.WriteLine($"[{Name}] Loaded {context.Ticket.Key}: {context.Ticket.Summary}");
        if (!string.IsNullOrWhiteSpace(context.TicketBrowseUrl))
            Console.WriteLine($"[{Name}] Browse {context.TicketBrowseUrl}");
    }
}

public sealed class ResolveProjectStep(AutoCoderOptions options) : IPipelineStep
{
    public string Name => "ResolveProject";

    public Task ExecuteAsync(PipelineContext context, CancellationToken cancellationToken = default)
    {
        var ticket = context.Ticket ?? throw new InvalidOperationException("Ticket required.");
        var projectHint = context.Items.TryGetValue("projectName", out var pn) ? pn?.ToString() : context.ProjectName;
        var resolved = ProjectCatalog.Resolve(options, ticket, projectHint);

        context.ProjectName = resolved.ProjectName;
        context.RepoUrl = resolved.Repo.Url;
        context.BaseBranch = string.IsNullOrWhiteSpace(resolved.Repo.DefaultBranch)
            ? "main"
            : resolved.Repo.DefaultBranch;
        context.JiraBaseUrl = resolved.JiraBaseUrl;
        context.TicketBrowseUrl = ProjectCatalog.BrowseUrl(resolved.JiraBaseUrl, ticket.Key);
        context.DoneStatus = resolved.Tracker.DoneStatus ?? "In Review";
        context.FailedStatus = string.IsNullOrWhiteSpace(resolved.Tracker.FailedStatus)
            ? "Agent Failure"
            : resolved.Tracker.FailedStatus;
        context.RunningStatus = string.IsNullOrWhiteSpace(resolved.Tracker.RunningStatus)
            ? "AgentWorking"
            : resolved.Tracker.RunningStatus;

        Console.WriteLine(
            $"[{Name}] Project={context.ProjectName} repo={context.RepoUrl} jira={context.JiraBaseUrl} "
            + $"(labels: {string.Join(", ", ticket.Labels)})");
        return Task.CompletedTask;
    }
}

public sealed class GeneratePlanStep(ILlmProvider llm) : IPipelineStep
{
    public string Name => "GeneratePlan";

    public async Task ExecuteAsync(PipelineContext context, CancellationToken cancellationToken = default)
    {
        var ticket = context.Ticket ?? throw new InvalidOperationException("Ticket required.");
        var prompt = $"""
            Ticket brief:
            {context.TicketBrief ?? $"{ticket.Key}: {ticket.Summary}\n{ticket.Description}"}

            Cheap-model repo scout (from the cloned allow-listed repo — treat paths as ground truth):
            {context.RepoScout ?? "(no scout)"}
            """;

        var response = await llm.CompleteAsync(new LlmRequest
        {
            ModelRole = "planning",
            MaxTokens = 4096,
            Messages =
            [
                new LlmMessage
                {
                    Role = "system",
                    Content = """
                        You are AutoCoder's planner. The repo has already been cloned and scouted.
                        Produce a concise implementation plan using ONLY real paths from the scout.
                        Name the tech stack, files to edit, tests to add/update, and risks.
                        Do not invent files or frameworks that the scout did not mention.
                        """
                },
                new LlmMessage { Role = "user", Content = prompt }
            ]
        }, cancellationToken);

        context.Plan = new ImplementationPlan
        {
            Summary = $"{ticket.IssueType ?? "Work"} {ticket.Key}: {ticket.Summary}",
            Steps = [],
            FilesLikelyTouched = [],
            Risks = [],
            TestPlan = [],
            RawMarkdown = response.Content
        };

        context.Items["promptTokens"] = response.PromptTokens;
        context.Items["completionTokens"] = response.CompletionTokens;
        context.Items["estimatedUsd"] = response.EstimatedUsdCost;

        Console.WriteLine($"[{Name}] Plan ready ({response.PromptTokens}+{response.CompletionTokens} tokens, ${response.EstimatedUsdCost:F4})");
    }
}

public sealed class ApprovalGateStep(IApprovalGate gate) : IPipelineStep
{
    public string Name => "ApprovalGate";

    public async Task ExecuteAsync(PipelineContext context, CancellationToken cancellationToken = default)
    {
        var plan = context.Plan ?? throw new InvalidOperationException("Plan required.");
        context.Approval = await gate.RequestApprovalAsync(plan, cancellationToken);
        if (context.Approval.Decision != ApprovalDecision.Approved)
        {
            context.FailureReason = $"Plan not approved: {context.Approval.Decision} ({context.Approval.Notes})";
            throw new InvalidOperationException(context.FailureReason);
        }

        Console.WriteLine($"[{Name}] Approved ({context.Approval.Notes})");
    }
}

public sealed class ProvisionSandboxStep(ISandboxRunner sandbox, IRepoHost repoHost) : IPipelineStep
{
    public string Name => "ProvisionSandbox";

    public async Task ExecuteAsync(PipelineContext context, CancellationToken cancellationToken = default)
    {
        var ticket = context.Ticket ?? throw new InvalidOperationException("Ticket required.");
        var repo = context.RepoUrl ?? throw new InvalidOperationException("RepoUrl required.");
        var work = Path.Combine(context.ArtifactsDirectory, context.RunId, "workspace");
        Directory.CreateDirectory(Path.GetDirectoryName(work)!);

        context.WorkDirectory = work;
        context.BranchName = $"autocoder/{ticket.Key.ToLowerInvariant()}";

        await sandbox.ProvisionAsync(new SandboxSpec
        {
            WorkDirectory = work,
            Image = "mcr.microsoft.com/dotnet/sdk:8.0",
            CommandAllowlist = ["dotnet", "git", "npm", "python", "pytest"]
        }, cancellationToken);

        await repoHost.CloneAndBranchAsync(
            repo,
            work,
            context.BranchName,
            context.BaseBranch,
            cancellationToken);

        Console.WriteLine($"[{Name}] Ready → {work} ({context.BranchName})");
    }
}

public sealed class AgenticImplementStep(AutoCoderOptions options) : IPipelineStep
{
    public string Name => "AgenticImplement";

    public async Task ExecuteAsync(PipelineContext context, CancellationToken cancellationToken = default)
    {
        var work = context.WorkDirectory ?? throw new InvalidOperationException("WorkDirectory required.");
        var ticket = context.Ticket ?? throw new InvalidOperationException("Ticket required.");

        var runDir = Path.Combine(work, ".autocoder", "runs", context.RunId);
        Directory.CreateDirectory(runDir);
        await File.WriteAllTextAsync(Path.Combine(runDir, "plan.md"), context.Plan?.RawMarkdown ?? "", cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(runDir, "ticket.md"),
            $"# {ticket.Key}\n\n{ticket.Summary}\n\n{ticket.Description}\n",
            cancellationToken);

        if (context.DryRun)
        {
            Console.WriteLine($"[{Name}] Dry-run: skipping coding agent (no real clone).");
            context.ProductFilesChanged = 0;
            return;
        }

        await new CodingAgentLoop(options).RunAsync(context, cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(runDir, "agent-summary.md"),
            context.AgentSummary ?? "",
            cancellationToken);
        Console.WriteLine($"[{Name}] Agent finished. product files={context.ProductFilesChanged}");
    }
}

public sealed class BuildStep(AutoCoderOptions options, ISandboxRunner sandbox) : IPipelineStep
{
    public string Name => "Build";

    public async Task ExecuteAsync(PipelineContext context, CancellationToken cancellationToken = default)
    {
        if (context.DryRun)
        {
            context.BuildSucceeded = true;
            Console.WriteLine($"[{Name}] Dry-run skipped.");
            return;
        }

        var work = context.WorkDirectory ?? throw new InvalidOperationException("WorkDirectory required.");
        var gates = PipelineGates.For(options, context.PipelineName);
        var ran = false;

        if (ProductStack.HasNode(work))
        {
            ran = true;
            var install = File.Exists(Path.Combine(work, "package-lock.json"))
                ? await sandbox.RunAllowlistedAsync("npm", ["ci"], cancellationToken)
                : await sandbox.RunAllowlistedAsync("npm", ["install"], cancellationToken);
            Console.WriteLine($"[{Name}] npm install/ci exit={install.ExitCode}");
            FailIf(install, "npm ci/install failed");
        }

        if (ProductStack.HasDotnet(work))
        {
            ran = true;
            var sln = ProductStack.DotnetBuildTarget(work)
                      ?? throw new InvalidOperationException("dotnet project vanished.");
            var rel = ProductStack.Rel(work, sln);
            var result = await sandbox.RunAllowlistedAsync("dotnet", ["build", rel, "--nologo", "-v", "q"], cancellationToken);
            Console.WriteLine($"[{Name}] dotnet build exit={result.ExitCode}");
            FailIf(result, "Build failed");
        }

        if (ProductStack.HasPython(work))
        {
            ran = true;
            var result = await sandbox.RunAllowlistedAsync("python", ["-m", "compileall", "-q", "."], cancellationToken);
            Console.WriteLine($"[{Name}] python compileall exit={result.ExitCode}");
            FailIf(result, "python compileall failed");
        }

        if (!ran)
        {
            if (gates.RequireBuild)
                throw new InvalidOperationException("No Node, .NET, or Python project found — require_build is true.");
            Console.WriteLine($"[{Name}] No supported project — require_build is false.");
        }

        context.BuildSucceeded = true;
    }

    private static void FailIf(SandboxCommandResult result, string title)
    {
        if (result.ExitCode == 0)
            return;
        throw new InvalidOperationException($"{title}:\n{result.StdOut}\n{result.StdErr}");
    }
}

public sealed class TestStep(AutoCoderOptions options, ISandboxRunner sandbox) : IPipelineStep
{
    public string Name => "Test";

    public async Task ExecuteAsync(PipelineContext context, CancellationToken cancellationToken = default)
    {
        if (context.DryRun)
        {
            context.TestsSucceeded = true;
            Console.WriteLine($"[{Name}] Dry-run skipped.");
            return;
        }

        var work = context.WorkDirectory ?? throw new InvalidOperationException("WorkDirectory required.");
        var gates = PipelineGates.For(options, context.PipelineName);
        var ran = false;

        if (ProductStack.HasNode(work))
        {
            if (!ProductStack.HasNpmTestScript(work))
            {
                if (gates.RequireTests)
                    throw new InvalidOperationException("package.json has no \"test\" script — require_tests is true. Add npm test.");
            }
            else
            {
                ran = true;
                var result = await sandbox.RunAllowlistedAsync("npm", ["test"], cancellationToken);
                Console.WriteLine($"[{Name}] npm test exit={result.ExitCode}");
                FailIf(result, "npm test failed");
            }
        }

        if (ProductStack.HasDotnet(work))
        {
            var target = ProductStack.DotnetTestTarget(work);
            if (target is null)
            {
                if (gates.RequireTests)
                    throw new InvalidOperationException("No .NET test project — require_tests is true.");
            }
            else
            {
                ran = true;
                var rel = ProductStack.Rel(work, target);
                var result = await sandbox.RunAllowlistedAsync("dotnet", ["test", rel, "--nologo"], cancellationToken);
                Console.WriteLine($"[{Name}] dotnet test exit={result.ExitCode}");
                FailIf(result, "Tests failed");
            }
        }

        if (ProductStack.HasPython(work))
        {
            if (!ProductStack.HasPythonTests(work))
            {
                if (gates.RequireTests)
                    throw new InvalidOperationException("No pytest files — require_tests is true.");
            }
            else
            {
                ran = true;
                var result = await sandbox.RunAllowlistedAsync("pytest", [], cancellationToken);
                Console.WriteLine($"[{Name}] pytest exit={result.ExitCode}");
                FailIf(result, "pytest failed");
            }
        }

        if (!ran && !ProductStack.Any(work) && gates.RequireTests)
            throw new InvalidOperationException("No Node, .NET, or Python tests found — require_tests is true.");

        context.TestsSucceeded = true;
    }

    private static void FailIf(SandboxCommandResult result, string title)
    {
        if (result.ExitCode == 0)
            return;
        throw new InvalidOperationException($"{title}:\n{result.StdOut}\n{result.StdErr}");
    }
}

public sealed class CommitAndOpenPrStep(IRepoHost repoHost) : IPipelineStep
{
    public string Name => "CommitAndOpenPr";

    public async Task ExecuteAsync(PipelineContext context, CancellationToken cancellationToken = default)
    {
        var ticket = context.Ticket ?? throw new InvalidOperationException("Ticket required.");
        var repo = context.RepoUrl ?? throw new InvalidOperationException("RepoUrl required.");
        var work = context.WorkDirectory ?? throw new InvalidOperationException("WorkDirectory required.");
        var branch = context.BranchName ?? $"autocoder/{ticket.Key.ToLowerInvariant()}";

        if (!context.DryRun)
        {
            if (context.ProductFilesChanged == 0)
                throw new InvalidOperationException("No product code changes — refusing PR.");
            if (!context.BuildSucceeded || !context.TestsSucceeded)
                throw new InvalidOperationException("Build/tests did not succeed — refusing PR.");
        }

        await repoHost.EnsureAllowlistedAsync(repo, cancellationToken);
        await repoHost.CommitAsync(new CommitRequest
        {
            RepoUrl = repo,
            Branch = branch,
            Message = $"{ticket.Key}: {ticket.Summary}",
            WorkDirectory = work
        }, cancellationToken);

        if (!context.DryRun)
            await repoHost.PushAsync(work, branch, cancellationToken);

        var body = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(context.TicketBrowseUrl))
            body.AppendLine($"Jira: {context.TicketBrowseUrl}");
        body.AppendLine($"Tracker ticket **{ticket.Key}**.");
        body.AppendLine();
        body.AppendLine("## Agent");
        body.AppendLine(context.AgentSummary ?? "(none)");
        body.AppendLine();
        body.AppendLine("## Plan");
        body.AppendLine(context.Plan?.RawMarkdown ?? "(none)");
        body.AppendLine();
        body.AppendLine("---");
        body.AppendLine("_Opened by AutoCoder. No auto-merge._");

        // Open as ready-for-review; never auto-merge.
        context.PullRequest = await repoHost.OpenPullRequestAsync(new PullRequestRequest
        {
            RepoUrl = repo,
            HeadBranch = branch,
            BaseBranch = context.BaseBranch,
            Title = $"{ticket.Key}: {ticket.Summary}",
            Body = body.ToString(),
            Draft = false
        }, cancellationToken);

        Console.WriteLine($"[{Name}] PR → {context.PullRequest.Url}");
    }
}

public sealed class SecretScanStep : IPipelineStep
{
    public string Name => "SecretScan";

    public Task ExecuteAsync(PipelineContext context, CancellationToken cancellationToken = default)
    {
        if (context.DryRun)
        {
            Console.WriteLine($"[{Name}] Dry-run skipped.");
            return Task.CompletedTask;
        }

        SecretScanner.Scan(context);
        return Task.CompletedTask;
    }
}

public sealed class WritebackTicketStep(ITicketSource ticketSource, ILlmProvider? llm = null) : IPipelineStep
{
    public string Name => "WritebackTicket";

    public async Task ExecuteAsync(PipelineContext context, CancellationToken cancellationToken = default)
    {
        var ticket = context.Ticket ?? throw new InvalidOperationException("Ticket required.");
        var failed = !string.IsNullOrWhiteSpace(context.FailureReason);
        var pr = context.PullRequest;
        string comment;
        string? status;

        if (failed)
        {
            status = string.IsNullOrWhiteSpace(context.FailedStatus) ? null : context.FailedStatus;
            comment = $"AutoCoder failed on {ticket.Key}.\n{context.FailureReason}";
        }
        else
        {
            status = string.IsNullOrWhiteSpace(context.DoneStatus) ? "In Review" : context.DoneStatus;
            var summary = await SummarizeForJiraAsync(context, cancellationToken);
            comment = pr is null
                ? "AutoCoder finished without a PR."
                : $"AutoCoder completed this ticket.\nPR: {pr.Url}\nBuild: {(context.BuildSucceeded ? "passed" : "n/a")}\nTests: {(context.TestsSucceeded ? "passed" : "n/a")}\n{summary}";
        }

        if (context.DryRun)
        {
            Console.WriteLine($"[{Name}] Dry-run writeback (not sent to Jira):");
            Console.WriteLine($"  status:  {status ?? "(unchanged)"}");
            Console.WriteLine($"  comment: {comment}");
            return;
        }

        try
        {
            await ticketSource.WritebackAsync(new TicketWriteback
            {
                TicketKey = ticket.Key,
                NewStatus = status,
                Comment = comment,
                LabelsToAdd = failed ? ["autocoder:failed"] : ["autocoder:done"]
            }, cancellationToken);
            Console.WriteLine($"[{Name}] Jira updated");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[{Name}] Jira writeback failed: {ex.Message}");
            if (!failed)
                throw;
        }
    }

    private async Task<string> SummarizeForJiraAsync(PipelineContext context, CancellationToken cancellationToken)
    {
        var raw = context.AgentSummary ?? "";
        if (context.DryRun || llm is null || raw.Length <= 280)
            return raw;

        try
        {
            var response = await llm.CompleteAsync(new LlmRequest
            {
                ModelRole = "summarize",
                MaxTokens = 400,
                Messages =
                [
                    new LlmMessage
                    {
                        Role = "system",
                        Content = "Rewrite the agent summary as 3-5 short Jira comment lines. No markdown headings."
                    },
                    new LlmMessage { Role = "user", Content = raw }
                ]
            }, cancellationToken);
            return string.IsNullOrWhiteSpace(response.Content) ? raw : response.Content.Trim();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{Name}] Cheap summarize skipped: {ex.Message}");
            return raw;
        }
    }
}

public sealed class PersistRunResultStep : IPipelineStep
{
    public string Name => "PersistRunResult";

    public async Task ExecuteAsync(PipelineContext context, CancellationToken cancellationToken = default)
    {
        var dir = Path.Combine(context.ArtifactsDirectory, context.RunId);
        Directory.CreateDirectory(dir);

        var planMd = context.Plan?.RawMarkdown ?? "(no plan)";
        await File.WriteAllTextAsync(Path.Combine(dir, "plan.md"), planMd, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(dir, "ticket-brief.md"), context.TicketBrief ?? "(none)", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(dir, "scout.md"), context.RepoScout ?? "(none)", cancellationToken);

        var decisions = $"""
            # Decisions

            - Pipeline: extract ticket → clone → cheap scout → costly plan → cheap implement → build → test → PR (no merge) → Jira writeback.
            - Dry run: {context.DryRun}
            - Product files changed: {context.ProductFilesChanged}
            - Build: {context.BuildSucceeded}  Tests: {context.TestsSucceeded}
            - Tokens: prompt={context.Spend.PromptTokens} completion={context.Spend.CompletionTokens} total={context.Spend.TotalTokens}
            - Tool calls: {context.Spend.ToolCalls}
            - Estimated USD: {context.Spend.EstimatedUsd:F4}
            - Done status: {context.DoneStatus ?? "In Review"}
            - Failed status: {context.FailedStatus ?? "Agent Failure"}
            - No auto-merge.
            """;
        await File.WriteAllTextAsync(Path.Combine(dir, "decisions.md"), decisions, cancellationToken);

        var spend = context.Spend;
        var result = $"""
            # Result

            - Run id: `{context.RunId}`
            - Pipeline: `{context.PipelineName}`
            - Ticket: `{context.Ticket?.Key}`
            - Outcome: {(context.FailureReason is null ? "success" : "failed")}
            - Failure: {context.FailureReason ?? "n/a"}
            - PR: {context.PullRequest?.Url ?? "n/a"}
            - Agent: {context.AgentSummary ?? "n/a"}
            - Dry run: {context.DryRun}
            - Tokens: prompt={spend.PromptTokens} completion={spend.CompletionTokens} total={spend.TotalTokens}
            - Tool calls: {spend.ToolCalls}
            - Estimated USD: {spend.EstimatedUsd:F4}
            """;
        await File.WriteAllTextAsync(Path.Combine(dir, "result.md"), result, cancellationToken);

        Console.WriteLine($"[{Name}] Artifacts written to {dir}");
    }
}
