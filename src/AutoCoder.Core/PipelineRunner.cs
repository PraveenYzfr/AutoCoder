using AutoCoder.Abstractions;

namespace AutoCoder.Core;

public sealed class PipelineRunner
{
    public async Task RunAsync(IPipeline pipeline, PipelineContext context, CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"AutoCoder run {context.RunId} · pipeline={pipeline.Name} · dryRun={context.DryRun}");
        Console.WriteLine();

        foreach (var step in pipeline.Steps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await step.ExecuteAsync(context, cancellationToken);
            }
            catch (Exception ex)
            {
                context.FailureReason ??= ex.Message;
                Console.Error.WriteLine($"Step '{step.Name}' failed: {ex.Message}");

                if (step.Name != "WritebackTicket")
                {
                    var writeback = pipeline.Steps.FirstOrDefault(s => s.Name == "WritebackTicket");
                    if (writeback is not null)
                    {
                        try { await writeback.ExecuteAsync(context, cancellationToken); }
                        catch (Exception wb)
                        {
                            Console.Error.WriteLine($"Writeback after failure: {wb.Message}");
                        }
                    }
                }

                if (step.Name != "PersistRunResult")
                {
                    var persist = pipeline.Steps.FirstOrDefault(s => s.Name == "PersistRunResult");
                    if (persist is not null)
                        await persist.ExecuteAsync(context, cancellationToken);
                }

                throw;
            }
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
