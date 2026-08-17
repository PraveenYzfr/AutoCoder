using AutoCoder.Abstractions;
using AutoCoder.Core.Agent;

namespace AutoCoder.Core.Pipelines;

/// <summary>Normalize Jira fields into a brief. No model call.</summary>
public sealed class ExtractTicketStep : IPipelineStep
{
    public string Name => "ExtractTicket";

    public Task ExecuteAsync(PipelineContext context, CancellationToken cancellationToken = default)
    {
        var ticket = context.Ticket ?? throw new InvalidOperationException("Ticket required.");
        context.TicketBrief = $"""
            Key: {ticket.Key}
            Type: {ticket.IssueType ?? "(unknown)"}
            Priority: {ticket.Priority ?? "(unset)"}
            Status: {ticket.Status}
            Summary: {ticket.Summary}
            Labels: {(ticket.Labels.Count == 0 ? "(none)" : string.Join(", ", ticket.Labels))}
            Browse: {context.TicketBrowseUrl ?? "(none)"}
            Target repo: {context.RepoUrl ?? "(unset)"}

            Description:
            {ticket.Description}

            Comments:
            {(ticket.Comments.Count == 0
                ? "(none)"
                : string.Join("\n", ticket.Comments.Select(c => $"- {c.Author}: {c.Body}")))}
            """;

        Console.WriteLine($"[{Name}] Ticket {ticket.Key} extracted ({context.TicketBrief.Length} chars).");
        return Task.CompletedTask;
    }
}

/// <summary>
/// Cheap model reads the cloned repo and writes a briefing for Claude.
/// Stack, layout, likely files — not an implementation plan.
/// </summary>
public sealed class ScoutRepoStep(ILlmProvider llm) : IPipelineStep
{
    public string Name => "ScoutRepo";

    public async Task ExecuteAsync(PipelineContext context, CancellationToken cancellationToken = default)
    {
        var ticket = context.Ticket ?? throw new InvalidOperationException("Ticket required.");
        var work = context.WorkDirectory ?? throw new InvalidOperationException("WorkDirectory required.");

        if (context.DryRun)
        {
            context.RepoScout = "(dry-run: no real clone; scout skipped)";
            Console.WriteLine($"[{Name}] Dry-run skipped.");
            return;
        }

        var tools = new WorkspaceTools(work);
        var facts = GatherFacts(work, tools, ticket);
        var response = await llm.CompleteAsync(new LlmRequest
        {
            ModelRole = "scout",
            MaxTokens = 1500,
            Messages =
            [
                new LlmMessage
                {
                    Role = "system",
                    Content = """
                        You scout a cloned application repo for a coding planner.
                        Report only what the files show: tech stack, project layout, test projects,
                        and which paths likely relate to the ticket. Quote real relative paths.
                        Do not write an implementation plan. Do not invent files that are not listed.
                        """
                },
                new LlmMessage
                {
                    Role = "user",
                    Content = $"""
                        Ticket:
                        {context.TicketBrief}

                        Repo facts (from disk):
                        {facts}
                        """
                }
            ]
        }, cancellationToken);

        context.RepoScout = response.Content;
        context.Items["scoutPromptTokens"] = response.PromptTokens;
        context.Items["scoutCompletionTokens"] = response.CompletionTokens;

        var runDir = Path.Combine(work, ".autocoder", "runs", context.RunId);
        Directory.CreateDirectory(runDir);
        await File.WriteAllTextAsync(Path.Combine(runDir, "scout.md"), context.RepoScout, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(runDir, "ticket-brief.md"), context.TicketBrief ?? "", cancellationToken);

        Console.WriteLine($"[{Name}] Scout ready ({response.PromptTokens}+{response.CompletionTokens} tokens).");
    }

    private static string GatherFacts(string work, WorkspaceTools tools, Ticket ticket)
    {
        var tree = tools.ListTree(250);
        var named = new[]
        {
            "README.md", "readme.md", "README",
            "Directory.Build.props", "global.json", "nuget.config",
            "package.json", "pyproject.toml"
        };
        var projects = Directory.EnumerateFiles(work, "*", SearchOption.AllDirectories)
            .Select(f => WorkspacePaths.Relativize(work, f))
            .Where(rel => !WorkspacePaths.IsIgnored(rel))
            .Where(rel =>
                rel.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                || rel.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                || rel.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
                || rel.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase))
            .Take(8);

        var reads = new List<string>();
        foreach (var path in named.Concat(projects).Distinct(StringComparer.OrdinalIgnoreCase).Take(12))
        {
            var body = tools.ReadFile(path, 6_000);
            if (body.StartsWith("File not found:", StringComparison.Ordinal))
                continue;
            reads.Add($"--- {path} ---\n{body}");
        }

        var greps = new List<string>();
        foreach (var term in Keywords(ticket).Take(5))
        {
            var hits = tools.Grep(term, ".");
            if (string.IsNullOrWhiteSpace(hits) || hits.Equals("No matches.", StringComparison.Ordinal))
                continue;
            greps.Add($"grep '{term}':\n{Truncate(hits, 1_500)}");
        }

        return $"""
            File tree:
            {tree}

            Key files:
            {(reads.Count == 0 ? "(none matched)" : string.Join("\n\n", reads))}

            Keyword hits:
            {(greps.Count == 0 ? "(none)" : string.Join("\n\n", greps))}
            """;
    }

    private static IEnumerable<string> Keywords(Ticket ticket)
    {
        var blob = $"{ticket.Summary} {ticket.Description}";
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "and", "for", "with", "from", "this", "that", "have", "should",
            "when", "into", "ticket", "please", "error", "issue", "fix"
        };
        return blob
            .Split([' ', '\n', '\r', '\t', ',', '.', ';', ':', '/', '\\', '(', ')', '[', ']', '"', '\'', '`'],
                StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim())
            .Where(w => w.Length >= 4 && !skip.Contains(w) && w.Any(char.IsLetterOrDigit))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "\n… truncated …";
}
