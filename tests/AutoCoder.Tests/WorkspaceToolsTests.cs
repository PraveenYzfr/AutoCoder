using AutoCoder.Core.Agent;

namespace AutoCoder.Tests;

public sealed class WorkspaceToolsTests : IDisposable
{
    private readonly string _work;
    private readonly WorkspaceTools _tools;

    public WorkspaceToolsTests()
    {
        _work = Path.Combine(Path.GetTempPath(), "autocoder-tools", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_work);
        _tools = new WorkspaceTools(_work);
    }

    [Fact]
    public void WriteFile_refuses_dotautocoder_and_does_not_count_it()
    {
        var result = _tools.WriteFile(".autocoder/runs/x/plan.md", "sneaky");
        Assert.Contains("Refusing to write under .autocoder", result);
        Assert.False(File.Exists(Path.Combine(_work, ".autocoder", "runs", "x", "plan.md")));
        Assert.Equal(0, _tools.ProductChangeCount);
    }

    [Fact]
    public void WriteFile_refuses_dotgit()
    {
        var result = _tools.WriteFile(".git/hooks/pre-commit", "malicious");
        Assert.Contains("Refusing to write under .git", result);
        Assert.False(File.Exists(Path.Combine(_work, ".git", "hooks", "pre-commit")));
    }

    [Fact]
    public void WriteFile_writes_product_files_and_tracks_them()
    {
        var result = _tools.WriteFile("public/landing/index.html", "<h1>Workflow</h1>");
        Assert.Contains("Wrote public/landing/index.html", result);
        Assert.Equal("<h1>Workflow</h1>", File.ReadAllText(Path.Combine(_work, "public", "landing", "index.html")));
        Assert.Equal(1, _tools.ProductChangeCount);
        Assert.Contains("public/landing/index.html", _tools.ChangedRelativePaths);
    }

    [Fact]
    public void WriteFile_rejects_path_traversal()
    {
        Assert.Throws<InvalidOperationException>(() => _tools.WriteFile("../outside.txt", "x"));
    }

    [Fact]
    public void ReadFile_reports_missing_file()
    {
        Assert.Contains("File not found", _tools.ReadFile("nope.txt"));
    }

    [Fact]
    public void ReadFile_truncates_beyond_max_chars()
    {
        File.WriteAllText(Path.Combine(_work, "big.txt"), new string('x', 100));
        var result = _tools.ReadFile("big.txt", maxChars: 10);
        Assert.StartsWith(new string('x', 10), result);
        Assert.Contains("truncated", result);
    }

    [Fact]
    public void ListFiles_marks_directory_entries_with_a_trailing_slash()
    {
        Directory.CreateDirectory(Path.Combine(_work, "src"));
        File.WriteAllText(Path.Combine(_work, "readme.txt"), "hi");

        var listing = _tools.ListFiles(".");
        Assert.Contains("src/", listing);
        Assert.Contains("readme.txt", listing);
    }

    [Fact]
    public void ListTree_excludes_files_nested_under_node_modules_git_and_build_output()
    {
        Directory.CreateDirectory(Path.Combine(_work, "node_modules", "left-pad"));
        File.WriteAllText(Path.Combine(_work, "node_modules", "left-pad", "index.js"), "x");
        Directory.CreateDirectory(Path.Combine(_work, "bin", "Debug"));
        File.WriteAllText(Path.Combine(_work, "bin", "Debug", "App.dll"), "x");
        Directory.CreateDirectory(Path.Combine(_work, "src"));
        File.WriteAllText(Path.Combine(_work, "src", "app.js"), "code");

        var tree = _tools.ListTree();
        Assert.DoesNotContain("index.js", tree);
        Assert.DoesNotContain("App.dll", tree);
        Assert.Contains("src/app.js", tree);
    }

    [Fact]
    public void ListFiles_reports_missing_directory()
    {
        Assert.Contains("Directory not found", _tools.ListFiles("does-not-exist"));
    }

    [Fact]
    public void Grep_finds_matches_with_line_numbers_and_skips_ignored_paths()
    {
        Directory.CreateDirectory(Path.Combine(_work, "src"));
        File.WriteAllLines(Path.Combine(_work, "src", "app.js"), ["const x = 1;", "// Safe change flow", "done"]);
        Directory.CreateDirectory(Path.Combine(_work, "node_modules"));
        File.WriteAllText(Path.Combine(_work, "node_modules", "lib.js"), "Safe change flow");

        var hits = _tools.Grep("Safe change flow");
        Assert.Contains("src/app.js:2:", hits);
        Assert.DoesNotContain("node_modules", hits);
    }

    [Fact]
    public void Grep_without_pattern_asks_for_one()
    {
        Assert.Equal("pattern required", _tools.Grep(""));
    }

    [Fact]
    public void Grep_reports_no_matches()
    {
        Directory.CreateDirectory(Path.Combine(_work, "src"));
        File.WriteAllText(Path.Combine(_work, "src", "app.js"), "nothing interesting");
        Assert.Equal("No matches.", _tools.Grep("needle"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_work, recursive: true); } catch { /* ignore */ }
    }
}
