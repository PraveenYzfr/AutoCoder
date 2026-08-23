using System.Globalization;
using System.Text.Json;

namespace AutoCoder.Core.Runs;

/// <summary>
/// Disk retention for the runs/ directory. Policy agreed with Praveen 2026-08-22
/// (see CLAUDE-AUTOCODER.md item 7):
///
/// - workspace/ (repo clone incl. node_modules) is deleted immediately once a run succeeds —
///   the diff already lives in the PR, so the clone has no further value.
/// - workspace/ is kept for the most recent <see cref="KeepFailedWorkspaces"/> failed runs only —
///   it is the only record of what the agent wrote before the run died.
/// - Run metadata (run.log, plan.md, scout.md, ticket-brief.md, result.md — a few tens of KB each)
///   is kept for the most recent <see cref="KeepMetadataRuns"/> runs; older run directories are
///   removed entirely. This is what the dashboard's history depends on.
///
/// Nothing here touches "leases" or other non-run-id entries under the root.
/// </summary>
public static class RunRetention
{
    public const int KeepFailedWorkspaces = 3;
    public const int KeepMetadataRuns = 100;

    /// <summary>Sweep one runs root. Safe to call after every run and at process startup.</summary>
    public static void Apply(string? artifactsRoot)
    {
        if (string.IsNullOrWhiteSpace(artifactsRoot))
            return;

        try
        {
            ApplyCore(artifactsRoot);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[retention] Sweep of '{artifactsRoot}' failed: {ex.Message}");
        }
    }

    private static void ApplyCore(string artifactsRoot)
    {
        if (!Directory.Exists(artifactsRoot))
            return;

        var runs = Directory.GetDirectories(artifactsRoot)
            .Select(Describe)
            .Where(r => r is not null)
            .Cast<RunDirInfo>()
            .OrderByDescending(r => r.StartedUtc ?? DateTime.MinValue)
            .ToList();

        foreach (var run in runs.Where(r => r.Status == "succeeded"))
            DeleteWorkspace(run);

        var failed = runs.Where(r => r.Status == "failed").ToList();
        foreach (var run in failed.Skip(KeepFailedWorkspaces))
            DeleteWorkspace(run);

        foreach (var run in runs.Skip(KeepMetadataRuns))
            DeleteRunDirectory(run);
    }

    private static void DeleteWorkspace(RunDirInfo run)
    {
        if (!Directory.Exists(run.WorkspacePath))
            return;
        try
        {
            Directory.Delete(run.WorkspacePath, recursive: true);
            Console.WriteLine($"[retention] Deleted workspace for {run.RunId} ({run.Status}).");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[retention] Delete workspace for {run.RunId} failed: {ex.Message}");
        }
    }

    private static void DeleteRunDirectory(RunDirInfo run)
    {
        try
        {
            Directory.Delete(run.Path, recursive: true);
            Console.WriteLine($"[retention] Pruned run {run.RunId} (beyond {KeepMetadataRuns} most recent).");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[retention] Prune run {run.RunId} failed: {ex.Message}");
        }
    }

    /// <summary>Only directories that look like a run (have run.log or result.md) are ever touched.</summary>
    private static RunDirInfo? Describe(string dir)
    {
        var logPath = Path.Combine(dir, "run.log");
        var resultPath = Path.Combine(dir, "result.md");
        if (!File.Exists(logPath) && !File.Exists(resultPath))
            return null;

        var (status, started) = ReadLog(logPath);
        return new RunDirInfo(
            Path.GetFileName(dir),
            dir,
            Path.Combine(dir, "workspace"),
            status,
            started);
    }

    private static (string Status, DateTime? StartedUtc) ReadLog(string logPath)
    {
        if (!File.Exists(logPath))
            return ("unknown", null);

        DateTime? started = null;
        var status = "running";
        foreach (var line in File.ReadLines(logPath))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (started is null
                    && root.TryGetProperty("ts", out var ts)
                    && ts.ValueKind == JsonValueKind.String
                    && DateTime.TryParse(ts.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
                {
                    started = parsed.ToUniversalTime();
                }

                var evt = root.TryGetProperty("event", out var e) ? e.GetString() : null;
                if (evt == "run.succeeded")
                    status = "succeeded";
                else if (evt == "run.failed")
                    status = "failed";
            }
            catch
            {
                // Torn last line on a still-running run — ignore and keep the status seen so far.
            }
        }

        return (status, started);
    }

    private sealed record RunDirInfo(string RunId, string Path, string WorkspacePath, string Status, DateTime? StartedUtc);
}
