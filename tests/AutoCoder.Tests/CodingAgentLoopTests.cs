using AutoCoder.Abstractions;
using AutoCoder.Core.Agent;

namespace AutoCoder.Tests;

public sealed class CodingAgentLoopTests : IDisposable
{
    private readonly string _work;

    public CodingAgentLoopTests()
    {
        _work = Path.Combine(Path.GetTempPath(), "autocoder-agent", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_work);
        File.WriteAllText(Path.Combine(_work, "README.md"), "# sample\n");
        File.WriteAllText(Path.Combine(_work, "app.js"), "console.log('hi');\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_work, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task RunTurnsAsync_writes_product_file_and_finishes_via_fake_client()
    {
        var context = TestContext.New(_work);
        context.DryRun = true;
        context.Ticket = TestContext.Ticket();
        context.Plan = new ImplementationPlan
        {
            Summary = "Update app.js greeting",
            RawMarkdown = "Edit app.js"
        };
        var tools = new WorkspaceTools(_work);
        var client = new ScriptedCodingToolClient(
        [
            new CodingTurn
            {
                Parts =
                [
                    new CodingPart
                    {
                        FunctionName = "write_file",
                        FunctionArgsJson = """{"path":"app.js","content":"console.log('bye');\n"}""",
                        ToolCallId = "call_1"
                    }
                ]
            },
            new CodingTurn
            {
                Parts =
                [
                    new CodingPart
                    {
                        FunctionName = "finish",
                        FunctionArgsJson = """{"summary":"Updated greeting"}""",
                        ToolCallId = "call_2"
                    }
                ]
            }
        ]);

        await CodingAgentLoop.RunTurnsAsync(context, context.Ticket!, tools, client, maxTurns: 5, CancellationToken.None);

        Assert.Equal("console.log('bye');\n", File.ReadAllText(Path.Combine(_work, "app.js")));
        Assert.Equal(1, context.ProductFilesChanged);
        Assert.Contains("app.js", context.ChangedRelativePaths);
        Assert.Equal("Updated greeting", context.AgentSummary);
        Assert.Equal(2, client.GenerateCalls);
        Assert.Equal(2, client.ToolRounds);
        Assert.Equal(0, client.Nudges);
    }

    [Fact]
    public async Task RunTurnsAsync_nudges_when_model_returns_text_without_tools_then_recovers()
    {
        var context = TestContext.New(_work);
        context.DryRun = true;
        context.Ticket = TestContext.Ticket();
        var tools = new WorkspaceTools(_work);
        var client = new ScriptedCodingToolClient(
        [
            new CodingTurn { Parts = [new CodingPart { Text = "Thinking about the change..." }] },
            new CodingTurn
            {
                Parts =
                [
                    new CodingPart
                    {
                        FunctionName = "write_file",
                        FunctionArgsJson = """{"path":"app.js","content":"ok"}""",
                        ToolCallId = "w1"
                    },
                    new CodingPart
                    {
                        FunctionName = "finish",
                        FunctionArgsJson = """{"summary":"done"}""",
                        ToolCallId = "f1"
                    }
                ]
            }
        ]);

        await CodingAgentLoop.RunTurnsAsync(context, context.Ticket!, tools, client, maxTurns: 5, CancellationToken.None);

        Assert.Equal(1, client.Nudges);
        Assert.Equal("done", context.AgentSummary);
        Assert.Equal(1, context.ProductFilesChanged);
    }

    [Fact]
    public async Task RunTurnsAsync_refuses_markdown_only_changes_when_not_dry_run()
    {
        var context = TestContext.New(_work);
        context.DryRun = false;
        context.Ticket = TestContext.Ticket();
        var tools = new WorkspaceTools(_work);
        var client = new ScriptedCodingToolClient(
        [
            new CodingTurn
            {
                Parts =
                [
                    new CodingPart
                    {
                        FunctionName = "write_file",
                        FunctionArgsJson = """{"path":"README.md","content":"# only docs"}""",
                        ToolCallId = "m1"
                    },
                    new CodingPart
                    {
                        FunctionName = "finish",
                        FunctionArgsJson = """{"summary":"docs only"}""",
                        ToolCallId = "f1"
                    }
                ]
            }
        ]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CodingAgentLoop.RunTurnsAsync(context, context.Ticket!, tools, client, maxTurns: 3, CancellationToken.None));

        Assert.Contains("markdown", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ScriptedCodingToolClient : ICodingToolClient
    {
        private readonly Queue<CodingTurn> _turns;

        public ScriptedCodingToolClient(IEnumerable<CodingTurn> turns) => _turns = new Queue<CodingTurn>(turns);

        public string ProviderName => "fake";
        public string Model => "scripted";
        public int GenerateCalls { get; private set; }
        public int ToolRounds { get; private set; }
        public int Nudges { get; private set; }

        public Task<CodingTurn> GenerateAsync(string system, List<object> history, CancellationToken cancellationToken)
        {
            GenerateCalls++;
            if (_turns.Count == 0)
                throw new InvalidOperationException("Scripted client ran out of turns.");
            return Task.FromResult(_turns.Dequeue());
        }

        public void AppendToolRound(List<object> history, CodingTurn turn, IReadOnlyList<CodingToolExecution> executions)
        {
            ToolRounds++;
            history.Add(new { role = "assistant", tools = executions.Count });
            history.Add(new { role = "user", results = executions.Count });
        }

        public void AppendNudge(List<object> history, CodingTurn turn, string nudge)
        {
            Nudges++;
            history.Add(new { role = "assistant", content = turn.CombinedText });
            history.Add(new { role = "user", content = nudge });
        }
    }
}
