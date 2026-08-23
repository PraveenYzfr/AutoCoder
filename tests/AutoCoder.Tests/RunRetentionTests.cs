using AutoCoder.Core.Runs;

namespace AutoCoder.Tests;

public sealed class RunRetentionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ac-retention", Guid.NewGuid().ToString("N"));

    public RunRetentionTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Succeeded_run_loses_its_workspace_but_keeps_metadata()
    {
        var dir = MakeRun("ok-run", "run.succeeded", DateTime.UtcNow, withWorkspace: true);

        RunRetention.Apply(_root);

        Assert.False(Directory.Exists(Path.Combine(dir, "workspace")));
        Assert.True(File.Exists(Path.Combine(dir, "run.log")));
    }

    [Fact]
    public void Failed_run_keeps_workspace_within_the_most_recent_three()
    {
        var now = DateTime.UtcNow;
        var newest = MakeRun("fail-1", "run.failed", now, withWorkspace: true);
        MakeRun("fail-2", "run.failed", now.AddMinutes(-1), withWorkspace: true);
        MakeRun("fail-3", "run.failed", now.AddMinutes(-2), withWorkspace: true);
        var oldest = MakeRun("fail-4", "run.failed", now.AddMinutes(-3), withWorkspace: true);

        RunRetention.Apply(_root);

        Assert.True(Directory.Exists(Path.Combine(newest, "workspace")));
        Assert.False(Directory.Exists(Path.Combine(oldest, "workspace")));
        // Metadata for the 4th-oldest failure is still kept — only its workspace is disposable.
        Assert.True(File.Exists(Path.Combine(oldest, "run.log")));
    }

    [Fact]
    public void Runs_beyond_the_metadata_cap_are_pruned_entirely()
    {
        var now = DateTime.UtcNow;
        for (var i = 0; i < RunRetention.KeepMetadataRuns + 5; i++)
            MakeRun($"run-{i:000}", "run.succeeded", now.AddMinutes(-i), withWorkspace: false);

        RunRetention.Apply(_root);

        var remaining = Directory.GetDirectories(_root);
        Assert.Equal(RunRetention.KeepMetadataRuns, remaining.Length);
        // The newest run (run-000) must survive; the oldest five must be gone.
        Assert.True(Directory.Exists(Path.Combine(_root, "run-000")));
        Assert.False(Directory.Exists(Path.Combine(_root, $"run-{RunRetention.KeepMetadataRuns + 4:000}")));
    }

    [Fact]
    public void Still_running_run_is_left_untouched()
    {
        var dir = MakeRun("live-run", null, DateTime.UtcNow, withWorkspace: true);

        RunRetention.Apply(_root);

        Assert.True(Directory.Exists(Path.Combine(dir, "workspace")));
    }

    [Fact]
    public void Missing_root_does_not_throw()
    {
        RunRetention.Apply(Path.Combine(_root, "does-not-exist"));
    }

    private string MakeRun(string runId, string? terminalEvent, DateTime startedUtc, bool withWorkspace)
    {
        var dir = Path.Combine(_root, runId);
        Directory.CreateDirectory(dir);
        var lines = new List<string>
        {
            $$"""{"ts":"{{startedUtc:O}}","event":"run.started","runId":"{{runId}}"}"""
        };
        if (terminalEvent is not null)
            lines.Add($$"""{"ts":"{{startedUtc:O}}","event":"{{terminalEvent}}"}""");
        File.WriteAllLines(Path.Combine(dir, "run.log"), lines);

        if (withWorkspace)
        {
            var workspace = Path.Combine(dir, "workspace");
            Directory.CreateDirectory(workspace);
            File.WriteAllText(Path.Combine(workspace, "marker.txt"), "clone");
        }

        return dir;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }
}
