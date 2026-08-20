using AutoCoder.Abstractions;
using AutoCoder.Core;

namespace AutoCoder.Tests;

public sealed class PipelineLoggingTests : IDisposable
{
    private readonly string _artifacts;

    public PipelineLoggingTests()
    {
        _artifacts = Path.Combine(Path.GetTempPath(), "autocoder-log", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_artifacts);
    }

    [Fact]
    public async Task Writes_jsonl_run_log_on_success_and_failure()
    {
        var ok = new RecordingPipeline("ok", new PassStep());
        var ctx = TestContext.New(artifacts: _artifacts);
        ctx = new PipelineContext
        {
            RunId = "ok-run",
            PipelineName = "fix-bug",
            ArtifactsDirectory = _artifacts,
            DryRun = true
        };
        await new PipelineRunner().RunAsync(ok, ctx);

        var log = Path.Combine(_artifacts, "ok-run", "run.log");
        Assert.True(File.Exists(log));
        var text = File.ReadAllText(log);
        Assert.Contains("run.started", text);
        Assert.Contains("step.succeeded", text);
        Assert.Contains("run.succeeded", text);

        var failCtx = new PipelineContext
        {
            RunId = "fail-run",
            PipelineName = "fix-bug",
            ArtifactsDirectory = _artifacts,
            DryRun = true
        };
        var boom = new RecordingPipeline("boom", new FailStep());
        await Assert.ThrowsAsync<InvalidOperationException>(() => new PipelineRunner().RunAsync(boom, failCtx));
        var failLog = File.ReadAllText(Path.Combine(_artifacts, "fail-run", "run.log"));
        Assert.Contains("step.failed", failLog);
        Assert.Contains("run.failed", failLog);
    }

    public void Dispose()
    {
        try { Directory.Delete(_artifacts, true); } catch { /* ignore */ }
    }

    private sealed class RecordingPipeline(string name, params IPipelineStep[] steps) : IPipeline
    {
        public string Name { get; } = name;
        public IReadOnlyList<IPipelineStep> Steps { get; } = steps;
    }

    private sealed class PassStep : IPipelineStep
    {
        public string Name => "Pass";
        public Task ExecuteAsync(PipelineContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FailStep : IPipelineStep
    {
        public string Name => "Boom";
        public Task ExecuteAsync(PipelineContext context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("boom");
    }
}
