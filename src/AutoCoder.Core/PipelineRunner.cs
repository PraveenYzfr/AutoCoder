using AutoCoder.Abstractions;
using AutoCoder.Abstractions.Config;
using AutoCoder.Core.Llm;
using AutoCoder.Core.Logging;
using AutoCoder.Core.Resilience;
using AutoCoder.Core.Runs;
using Microsoft.Extensions.Logging;

namespace AutoCoder.Core;

public sealed class PipelineRunner
{
    public Task RunAsync(IPipeline pipeline, PipelineContext context, CancellationToken cancellationToken = default) =>
        RunAsync(pipeline, context, options: null, cancellationToken);

    public async Task RunAsync(
        IPipeline pipeline,
        PipelineContext context,
        AutoCoderOptions? options,
        CancellationToken cancellationToken = default)
    {
        RunLog.Event("run.started", context, fields: ("pipeline", pipeline.Name));

        if (options is not null)
        {
            RunConcurrency.Configure(options.Limits);
            TransientRetry.Configure(options.Resilience);
        }

        using var budget = RunBudget.Enter(context, options?.Limits);
        using var slot = await RunConcurrency.AcquireAsync(cancellationToken);

        try
        {
            foreach (var step in pipeline.Steps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var started = DateTime.UtcNow;
                RunLog.Event("step.started", context, fields: ("step", step.Name));
                try
                {
                    await step.ExecuteAsync(context, cancellationToken);
                    budget.ThrowIfExceeded();
                    RunLog.Event(
                        "step.succeeded",
                        context,
                        fields: [("step", step.Name), ("ms", (DateTime.UtcNow - started).TotalMilliseconds)]);
                }
                catch (Exception ex)
                {
                    context.FailureReason ??= ex.Message;
                    context.FailureIsTransient = LlmFailureClassifier.IsTransient(ex);
                    RunLog.Event(
                        "step.failed",
                        context,
                        LogLevel.Error,
                        ex,
                        ("step", step.Name),
                        ("error", ex.Message),
                        ("ms", (DateTime.UtcNow - started).TotalMilliseconds),
                        ("transient", context.FailureIsTransient));

                    if (step.Name != "WritebackTicket")
                    {
                        var writeback = pipeline.Steps.FirstOrDefault(s => s.Name == "WritebackTicket");
                        if (writeback is not null)
                        {
                            try
                            {
                                await writeback.ExecuteAsync(context, cancellationToken);
                                RunLog.Event("step.succeeded", context, fields: [("step", writeback.Name), ("ms", 0)]);
                            }
                            catch (Exception wb)
                            {
                                RunLog.Event("writeback.failed", context, LogLevel.Error, wb,
                                    ("afterStep", step.Name), ("error", wb.Message));
                            }
                        }
                    }

                    if (step.Name != "PersistRunResult")
                    {
                        var persist = pipeline.Steps.FirstOrDefault(s => s.Name == "PersistRunResult");
                        if (persist is not null)
                        {
                            await persist.ExecuteAsync(context, cancellationToken);
                            RunLog.Event("step.succeeded", context, fields: [("step", persist.Name), ("ms", 0)]);
                        }
                    }

                    RunLog.Event("run.failed", context, LogLevel.Error, ex, ("step", step.Name), ("error", ex.Message));
                    throw;
                }
            }

            RunLog.Event("run.succeeded", context);
        }
        finally
        {
            // Disk retention (workspace cleanup + old-run pruning) — see RunRetention for policy.
            // Runs for every completed run (success or failure), not just on a timer.
            RunRetention.Apply(context.ArtifactsDirectory);
        }
    }

    public static string NewRunId(string? slug = null)
    {
        var stamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH-mm-ss");
        var hex = Guid.NewGuid().ToString("N")[..4];
        var safeSlug = string.IsNullOrWhiteSpace(slug)
            ? "run"
            : new string(slug.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
        return $"{stamp}-{hex}-{safeSlug}";
    }
}
