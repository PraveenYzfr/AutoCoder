using System.Text.Json;
using AutoCoder.Abstractions;
using AutoCoder.Abstractions.Config;
using AutoCoder.Core.Llm;
using AutoCoder.Core.Logging;
using AutoCoder.Core.Runs;

namespace AutoCoder.Core.Agent;

public sealed class CodingAgentLoop
{
    private const int MaxTurns = 40;
    private readonly AutoCoderOptions? _options;

    public CodingAgentLoop(AutoCoderOptions? options = null) => _options = options;

    public async Task RunAsync(PipelineContext context, CancellationToken cancellationToken = default)
    {
        var work = context.WorkDirectory ?? throw new InvalidOperationException("WorkDirectory required.");
        var ticket = context.Ticket ?? throw new InvalidOperationException("Ticket required.");
        var type = _options is null ? "deepseek" : LlmProviderFactory.ResolveCodingType(_options);
        var model = _options is null ? DeepSeekModels.Flash : LlmProviderFactory.ResolveCodingModel(_options);
        Console.WriteLine($"[agent] coding tier={type} model={model}");

        switch (type)
        {
            case "openai":
                await RunOpenAiCompatibleAsync(
                    context, work, ticket, cancellationToken,
                    "OPENAI_API_KEY",
                    model,
                    "https://api.openai.com/v1",
                    "openai");
                break;
            case "groq":
                await RunOpenAiCompatibleAsync(
                    context, work, ticket, cancellationToken,
                    "GROQ_API_KEY",
                    GroqModels.Sanitize(model),
                    GroqModels.BaseUrl,
                    "groq");
                break;
            case "gemini":
            case "google":
                await RunGeminiAsync(context, work, ticket, cancellationToken, model);
                break;
            case "anthropic":
            case "claude":
                await RunAnthropicAsync(context, work, ticket, cancellationToken, model);
                break;
            default:
                await RunOpenAiCompatibleAsync(
                    context, work, ticket, cancellationToken,
                    "DEEPSEEK_API_KEY",
                    DeepSeekModels.Sanitize(model),
                    "https://api.deepseek.com/v1",
                    "deepseek");
                break;
        }
    }

    private async Task RunGeminiAsync(
        PipelineContext context, string work, Ticket ticket, CancellationToken cancellationToken, string model)
    {
        var key = Environment.GetEnvironmentVariable("GEMINI_API_KEY")
                  ?? Environment.GetEnvironmentVariable("GOOGLE_API_KEY")
                  ?? throw new InvalidOperationException("GEMINI_API_KEY is required for the coding agent.");

        if (string.IsNullOrWhiteSpace(model)
            || model.Contains("deepseek", StringComparison.OrdinalIgnoreCase)
            || model.Contains("gpt-", StringComparison.OrdinalIgnoreCase)
            || model.Contains("claude", StringComparison.OrdinalIgnoreCase))
            model = "gemini-flash-lite-latest";

        var tools = new WorkspaceTools(work);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        var client = new GeminiToolClient(http, key, model);
        await RunGeminiTurnsAsync(context, ticket, tools, client, model, TurnCap(), cancellationToken);
    }

    private async Task RunAnthropicAsync(
        PipelineContext context, string work, Ticket ticket, CancellationToken cancellationToken, string model)
    {
        var key = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
                  ?? throw new InvalidOperationException("ANTHROPIC_API_KEY is required for the coding agent.");

        if (string.IsNullOrWhiteSpace(model)
            || model.Contains("deepseek", StringComparison.OrdinalIgnoreCase)
            || model.Contains("gpt-", StringComparison.OrdinalIgnoreCase)
            || model.Contains("gemini", StringComparison.OrdinalIgnoreCase))
            model = "claude-sonnet-5";

        var tools = new WorkspaceTools(work);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        var client = new AnthropicToolClient(http, key, model);
        await RunAnthropicTurnsAsync(context, ticket, tools, client, model, TurnCap(), cancellationToken);
    }

    private async Task RunOpenAiCompatibleAsync(
        PipelineContext context,
        string work,
        Ticket ticket,
        CancellationToken cancellationToken,
        string apiKeyEnv,
        string model,
        string baseUrl,
        string providerName)
    {
        var key = Environment.GetEnvironmentVariable(apiKeyEnv)
                  ?? throw new InvalidOperationException($"{apiKeyEnv} is required for the {providerName} coding agent.");
        var tools = new WorkspaceTools(work);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        var client = new OpenAiToolClient(http, key, model, baseUrl, providerName);
        await RunOpenAiTurnsAsync(context, ticket, tools, client, model, providerName, TurnCap(), cancellationToken);
    }

    private static (string System, string User, string Intent) Prompt(PipelineContext context, Ticket ticket, WorkspaceTools tools)
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

    private static async Task RunGeminiTurnsAsync(
        PipelineContext context, Ticket ticket, WorkspaceTools tools, GeminiToolClient client, string model, int maxTurns, CancellationToken cancellationToken)
    {
        var (system, user, intent) = Prompt(context, ticket, tools);
        var contents = new List<object>
        {
            new { role = "user", parts = new object[] { new { text = user } } }
        };

        Console.WriteLine($"[agent] Starting coding loop ({intent}) provider=gemini");
        LlmCallContext.CurrentRole = "coding";
        LlmCallContext.CurrentTier = "cheap";
        RunLog.Event(
            "agent.started",
            context,
            fields: [("provider", "gemini"), ("model", model), ("maxTurns", maxTurns), ("intent", intent)]);

        for (var turn = 1; turn <= maxTurns; turn++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RunLog.Event("agent.turn", context, fields: [("turn", turn), ("maxTurns", maxTurns)]);
            var reply = await client.GenerateAsync(system, contents, cancellationToken);
            var calls = reply.Parts.Where(p => p.IsFunction).ToList();
            var text = string.Join("\n", reply.Parts.Select(p => p.Text).Where(t => !string.IsNullOrWhiteSpace(t)));

            if (calls.Count == 0)
            {
                if (!string.IsNullOrWhiteSpace(text))
                    Console.WriteLine($"[agent] {text[..Math.Min(400, text.Length)]}");
                if (tools.ProductChangeCount > 0)
                {
                    context.AgentSummary = text;
                    break;
                }

                contents.Add(new { role = "model", parts = new object[] { new { text = text ?? "" } } });
                contents.Add(new
                {
                    role = "user",
                    parts = new object[]
                    {
                        new { text = "You have not changed product source yet. Use write_file, then finish." }
                    }
                });
                continue;
            }

            var modelParts = new List<object>();
            var fnResponses = new List<object>();
            var finished = false;

            foreach (var call in calls)
            {
                Console.WriteLine($"[agent] tool {call.FunctionName}");
                var argsDict = new Dictionary<string, object?>();
                try
                {
                    using var argsDoc = JsonDocument.Parse(call.FunctionArgs ?? "{}");
                    foreach (var prop in argsDoc.RootElement.EnumerateObject())
                        argsDict[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                            ? prop.Value.GetString()
                            : prop.Value.GetRawText();
                }
                catch
                {
                    // keep empty args
                }

                modelParts.Add(new
                {
                    functionCall = new
                    {
                        name = call.FunctionName,
                        args = argsDict
                    }
                });

                var result = Execute(context, tools, call.FunctionName!, call.FunctionArgs ?? "{}", turn);
                if (call.FunctionName == "finish")
                {
                    finished = true;
                    context.AgentSummary = result;
                }

                fnResponses.Add(new
                {
                    functionResponse = new
                    {
                        name = call.FunctionName,
                        response = new Dictionary<string, string> { ["result"] = result }
                    }
                });
            }

            contents.Add(new { role = "model", parts = modelParts });
            contents.Add(new { role = "user", parts = fnResponses });

            if (finished)
                break;
        }

        Finish(context, tools);
    }

    private static async Task RunAnthropicTurnsAsync(
        PipelineContext context, Ticket ticket, WorkspaceTools tools, AnthropicToolClient client, string model, int maxTurns, CancellationToken cancellationToken)
    {
        var (system, user, intent) = Prompt(context, ticket, tools);
        var messages = new List<object> { new { role = "user", content = user } };

        Console.WriteLine($"[agent] Starting coding loop ({intent}) provider=anthropic model={model}");
        LlmCallContext.CurrentRole = "coding";
        LlmCallContext.CurrentTier = "cheap";
        RunLog.Event(
            "agent.started",
            context,
            fields: [("provider", "anthropic"), ("model", model), ("maxTurns", maxTurns), ("intent", intent)]);

        for (var turn = 1; turn <= maxTurns; turn++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RunLog.Event("agent.turn", context, fields: [("turn", turn), ("maxTurns", maxTurns)]);
            var reply = await client.GenerateAsync(system, messages, cancellationToken);
            var calls = reply.Parts.Where(p => p.IsFunction).ToList();
            var text = string.Join("\n", reply.Parts.Select(p => p.Text).Where(t => !string.IsNullOrWhiteSpace(t)));

            if (calls.Count == 0)
            {
                if (!string.IsNullOrWhiteSpace(text))
                    Console.WriteLine($"[agent] {text[..Math.Min(400, text.Length)]}");
                if (tools.ProductChangeCount > 0)
                {
                    context.AgentSummary = text;
                    break;
                }

                messages.Add(new { role = "assistant", content = reply.RawContentBlocks });
                messages.Add(new
                {
                    role = "user",
                    content = "You have not changed product source yet. Use write_file, then finish."
                });
                continue;
            }

            var finished = false;
            var toolResults = new List<object>();
            foreach (var call in calls)
            {
                Console.WriteLine($"[agent] tool {call.FunctionName}");
                var result = Execute(context, tools, call.FunctionName!, call.FunctionArgsJson ?? "{}", turn);
                if (call.FunctionName == "finish")
                {
                    finished = true;
                    context.AgentSummary = result;
                }

                toolResults.Add(new
                {
                    type = "tool_result",
                    tool_use_id = call.ToolUseId,
                    content = result
                });
            }

            messages.Add(new { role = "assistant", content = reply.RawContentBlocks });
            messages.Add(new { role = "user", content = toolResults });

            if (finished)
                break;
        }

        Finish(context, tools);
    }

    private static async Task RunOpenAiTurnsAsync(
        PipelineContext context,
        Ticket ticket,
        WorkspaceTools tools,
        OpenAiToolClient client,
        string model,
        string providerName,
        int maxTurns,
        CancellationToken cancellationToken)
    {
        var (system, user, intent) = Prompt(context, ticket, tools);
        var messages = new List<object> { new { role = "user", content = user } };
        Console.WriteLine($"[agent] Starting coding loop ({intent}) provider={providerName} model={model}");
        LlmCallContext.CurrentRole = "coding";
        LlmCallContext.CurrentTier = "cheap";
        RunLog.Event(
            "agent.started",
            context,
            fields: [("provider", providerName), ("model", model), ("maxTurns", maxTurns), ("intent", intent)]);

        for (var turn = 1; turn <= maxTurns; turn++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RunLog.Event("agent.turn", context, fields: [("turn", turn), ("maxTurns", maxTurns)]);
            var reply = await client.GenerateAsync(system, messages, cancellationToken);
            var calls = reply.Parts.Where(p => p.IsFunction).ToList();
            var text = string.Join("\n", reply.Parts.Select(p => p.Text).Where(t => !string.IsNullOrWhiteSpace(t)));

            if (calls.Count == 0)
            {
                if (!string.IsNullOrWhiteSpace(text))
                    Console.WriteLine($"[agent] {text[..Math.Min(400, text.Length)]}");
                if (tools.ProductChangeCount > 0)
                {
                    context.AgentSummary = text;
                    break;
                }

                messages.Add(new { role = "assistant", content = text ?? "" });
                messages.Add(new { role = "user", content = "You have not changed product source yet. Use write_file, then finish." });
                continue;
            }

            var toolCalls = new List<object>();
            var toolResults = new List<object>();
            var finished = false;
            foreach (var call in calls)
            {
                Console.WriteLine($"[agent] tool {call.FunctionName}");
                var id = string.IsNullOrWhiteSpace(call.ToolCallId) ? Guid.NewGuid().ToString("N") : call.ToolCallId;
                toolCalls.Add(new
                {
                    id,
                    type = "function",
                    function = new { name = call.FunctionName, arguments = call.FunctionArgs ?? "{}" }
                });
                var result = Execute(context, tools, call.FunctionName!, call.FunctionArgs ?? "{}", turn);
                if (call.FunctionName == "finish")
                {
                    finished = true;
                    context.AgentSummary = result;
                }

                toolResults.Add(new { role = "tool", tool_call_id = id, content = result });
            }

            messages.Add(new { role = "assistant", content = text ?? "", tool_calls = toolCalls });
            messages.AddRange(toolResults);
            if (finished)
                break;
        }

        Finish(context, tools);
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
                "Agent only changed markdown (.md). Expected application source (HTML/JS/CSS/…). Refusing PR.");
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

    private static string Execute(PipelineContext context, WorkspaceTools tools, string name, string argsJson, int turn)
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
