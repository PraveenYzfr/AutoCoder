using System.Text.Json;
using AutoCoder.Abstractions;
using AutoCoder.Abstractions.Config;
using AutoCoder.Core.Llm;
using AutoCoder.Core.Logging;
using AutoCoder.Core.Runs;

namespace AutoCoder.Core.Agent;

/// <summary>
/// Provider-agnostic coding turn driver. Wire-format differences live in
/// <see cref="ICodingToolClient"/> implementations (OpenAI / Gemini / Anthropic).
/// </summary>
public sealed class CodingAgentLoop
{
    private const int MaxTurns = 40;
    private readonly AutoCoderOptions? _options;
    private readonly Func<string, string, HttpClient, ICodingToolClient>? _clientFactory;

    public CodingAgentLoop(AutoCoderOptions? options = null)
        : this(options, clientFactory: null)
    {
    }

    /// <summary>Test seam: inject a factory that returns a fake <see cref="ICodingToolClient"/>.</summary>
    internal CodingAgentLoop(
        AutoCoderOptions? options,
        Func<string, string, HttpClient, ICodingToolClient>? clientFactory)
    {
        _options = options;
        _clientFactory = clientFactory;
    }

    public async Task RunAsync(PipelineContext context, CancellationToken cancellationToken = default)
    {
        var work = context.WorkDirectory ?? throw new InvalidOperationException("WorkDirectory required.");
        var ticket = context.Ticket ?? throw new InvalidOperationException("Ticket required.");
        var type = _options is null ? "deepseek" : LlmProviderFactory.ResolveCodingType(_options);
        var model = _options is null ? DeepSeekModels.Flash : LlmProviderFactory.ResolveCodingModel(_options);
        Console.WriteLine($"[agent] coding tier={type} model={model}");

        var tools = new WorkspaceTools(work);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        var client = (_clientFactory ?? CodingToolClientFactory.Create)(type, model, http);
        await RunTurnsAsync(context, ticket, tools, client, TurnCap(), cancellationToken);
    }

    /// <summary>Single turn driver shared by every provider.</summary>
    internal static async Task RunTurnsAsync(
        PipelineContext context,
        Ticket ticket,
        WorkspaceTools tools,
        ICodingToolClient client,
        int maxTurns,
        CancellationToken cancellationToken)
    {
        var (system, user, intent) = Prompt(context, ticket, tools);
        var history = new List<object> { SeedUserMessage(client.ProviderName, user) };

        Console.WriteLine($"[agent] Starting coding loop ({intent}) provider={client.ProviderName} model={client.Model}");
        LlmCallContext.CurrentRole = "coding";
        LlmCallContext.CurrentTier = "cheap";
        RunLog.Event(
            "agent.started",
            context,
            fields:
            [
                ("provider", client.ProviderName),
                ("model", client.Model),
                ("maxTurns", maxTurns),
                ("intent", intent)
            ]);

        for (var turn = 1; turn <= maxTurns; turn++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RunLog.Event("agent.turn", context, fields: [("turn", turn), ("maxTurns", maxTurns)]);

            var reply = await client.GenerateAsync(system, history, cancellationToken);
            var calls = reply.FunctionCalls;
            var text = reply.CombinedText;

            if (calls.Count == 0)
            {
                if (!string.IsNullOrWhiteSpace(text))
                    Console.WriteLine($"[agent] {text[..Math.Min(400, text.Length)]}");
                if (tools.ProductChangeCount > 0)
                {
                    context.AgentSummary = text;
                    break;
                }

                client.AppendNudge(history, reply, CodingToolCatalog.NudgeNoProductChange);
                continue;
            }

            var executions = new List<CodingToolExecution>();
            var finished = false;
            foreach (var call in calls)
            {
                Console.WriteLine($"[agent] tool {call.FunctionName}");
                var id = string.IsNullOrWhiteSpace(call.ToolCallId)
                    ? Guid.NewGuid().ToString("N")
                    : call.ToolCallId!;
                var argsJson = call.FunctionArgsJson ?? "{}";
                var result = Execute(context, tools, call.FunctionName!, argsJson, turn);
                if (call.FunctionName == "finish")
                {
                    finished = true;
                    context.AgentSummary = result;
                }

                executions.Add(new CodingToolExecution(call.FunctionName!, argsJson, id, result));
            }

            client.AppendToolRound(history, reply, executions);
            if (finished)
                break;
        }

        Finish(context, tools);
    }

    private static object SeedUserMessage(string provider, string user) =>
        provider.Equals("gemini", StringComparison.OrdinalIgnoreCase)
            ? new { role = "user", parts = new object[] { new { text = user } } }
            : new { role = "user", content = user };

    private static (string System, string User, string Intent) Prompt(
        PipelineContext context, Ticket ticket, WorkspaceTools tools)
    {
        var intent = InferIntent(ticket);
        var system = $"""
            You are AutoCoder, an enterprise coding agent.
            Task type: {intent}.
            You MUST implement the approved plan by editing application source. Do not only write markdown.
            Follow the plan's file paths. Re-read files before writing. Do not invent a different approach
            unless a planned path does not exist.
            If the ticket updates existing UI copy (heading/list on a page), grep for that text and edit the
            HTML/JS file that contains it — do not recreate the same content in README.md.
            Workspace is a git checkout. Paths are relative to the repo root.
            Never run shell. Never write under .git or .autocoder/.
            Use list_files, grep, read_file to understand the repo, then write_file with complete file contents.
            Add or update tests when the repo has a test project.
            When the change is complete, call finish.
            """;
        var listing = tools.ListFiles(".");
        var user = $"""
            Ticket: {ticket.Key}
            Type: {ticket.IssueType}
            Summary: {ticket.Summary}
            Description:
            {ticket.Description}

            Repo scout:
            {context.RepoScout}

            Approved plan:
            {context.Plan?.RawMarkdown}

            Workspace top-level:
            {listing}

            Implement the approved plan now. Inspect the repo, edit product source, add tests if a test project exists, then finish.
            """;
        return (system, user, intent);
    }

    private int TurnCap()
    {
        var maxTools = _options?.Limits.MaxToolCalls ?? 0;
        return maxTools > 0 ? Math.Max(8, maxTools) : MaxTurns;
    }

    private static void Finish(PipelineContext context, WorkspaceTools tools)
    {
        context.ProductFilesChanged = tools.ProductChangeCount;
        context.ChangedRelativePaths.Clear();
        context.ChangedRelativePaths.AddRange(tools.ChangedRelativePaths);
        RunLog.Event(
            "agent.finished",
            context,
            fields:
            [
                ("files", context.ProductFilesChanged),
                ("paths", string.Join(",", context.ChangedRelativePaths)),
                ("finished", !string.IsNullOrWhiteSpace(context.AgentSummary))
            ]);
        Console.WriteLine($"[agent] Product files changed: {context.ProductFilesChanged}");
        if (context.ProductFilesChanged == 0 && !context.DryRun)
        {
            throw new InvalidOperationException(
                "Agent did not change any product source files. Refusing to open a PR.");
        }

        var product = context.ChangedRelativePaths.Where(WorkspacePaths.IsProductFile).ToList();
        if (!context.DryRun
            && product.Count > 0
            && product.All(p => p.EndsWith(".md", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "Agent only changed markdown (.md). Expected application source (HTML/JS/CSS/...). Refusing PR.");
        }
    }

    private static string InferIntent(Ticket ticket)
    {
        var blob = $"{ticket.IssueType} {ticket.Summary} {string.Join(' ', ticket.Labels)}";
        if (blob.Contains("feature", StringComparison.OrdinalIgnoreCase)
            || blob.Contains("story", StringComparison.OrdinalIgnoreCase)
            || blob.Contains("enhancement", StringComparison.OrdinalIgnoreCase))
            return "new feature";
        return "bug fix";
    }

    private static string Execute(
        PipelineContext context, WorkspaceTools tools, string name, string argsJson, int turn)
    {
        RunBudget.Current?.AddToolCalls(1);
        using var args = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        var root = args.RootElement;
        string S(string key)
        {
            if (!root.TryGetProperty(key, out var e))
                return "";
            return e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.GetRawText();
        }

        var path = S("path");
        RunLog.Event(
            "agent.tool",
            context,
            fields: [("tool", name), ("path", path), ("turn", turn), ("toolCalls", context.Spend.ToolCalls)]);

        return name switch
        {
            "list_files" => tools.ListFiles(path),
            "read_file" => tools.ReadFile(path),
            "write_file" => tools.WriteFile(path, S("content")),
            "grep" => tools.Grep(S("pattern"), path),
            "finish" => S("summary"),
            _ => $"Unknown tool {name}"
        };
    }
}
