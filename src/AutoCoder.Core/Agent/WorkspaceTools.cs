using System.Text;
using System.Text.Json;

namespace AutoCoder.Core.Agent;

internal sealed class WorkspaceTools
{
    private readonly string _work;
    private readonly HashSet<string> _changed = new(StringComparer.OrdinalIgnoreCase);

    public WorkspaceTools(string workDirectory) => _work = workDirectory;

    public IReadOnlyCollection<string> ChangedRelativePaths => _changed;

    public int ProductChangeCount => _changed.Count(WorkspacePaths.IsProductFile);

    public string ListFiles(string path, int max = 200)
    {
        var dir = WorkspacePaths.Resolve(_work, string.IsNullOrWhiteSpace(path) ? "." : path);
        if (!Directory.Exists(dir))
            return $"Directory not found: {path}";

        var entries = new List<string>();
        foreach (var item in Directory.EnumerateFileSystemEntries(dir).OrderBy(x => x))
        {
            var rel = WorkspacePaths.Relativize(_work, item);
            if (WorkspacePaths.IsIgnored(rel))
                continue;
            var suffix = Directory.Exists(item) ? "/" : "";
            entries.Add(rel + suffix);
            if (entries.Count >= max)
            {
                entries.Add("… truncated …");
                break;
            }
        }

        return entries.Count == 0 ? "(empty)" : string.Join('\n', entries);
    }

    public string ListTree(int max = 250)
    {
        if (!Directory.Exists(_work))
            return "(no workspace)";

        var entries = new List<string>();
        foreach (var item in Directory.EnumerateFileSystemEntries(_work, "*", SearchOption.AllDirectories).OrderBy(x => x))
        {
            var rel = WorkspacePaths.Relativize(_work, item);
            if (WorkspacePaths.IsIgnored(rel))
                continue;
            var suffix = Directory.Exists(item) ? "/" : "";
            entries.Add(rel + suffix);
            if (entries.Count >= max)
            {
                entries.Add("… truncated …");
                break;
            }
        }

        return entries.Count == 0 ? "(empty)" : string.Join('\n', entries);
    }

    public string ReadFile(string path, int maxChars = 80_000)
    {
        var full = WorkspacePaths.Resolve(_work, path);
        if (!File.Exists(full))
            return $"File not found: {path}";
        var text = File.ReadAllText(full);
        if (text.Length > maxChars)
            return text[..maxChars] + "\n… truncated …";
        return text;
    }

    public string WriteFile(string path, string content)
    {
        var full = WorkspacePaths.Resolve(_work, path);
        var rel = WorkspacePaths.Relativize(_work, full);
        if (rel.StartsWith(".git", StringComparison.OrdinalIgnoreCase))
            return "Refusing to write under .git";
        if (rel.StartsWith(".autocoder", StringComparison.OrdinalIgnoreCase)
            || rel.Contains("/.autocoder/", StringComparison.OrdinalIgnoreCase))
            return "Refusing to write under .autocoder (AutoCoder internals, not product code).";

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content ?? "", Encoding.UTF8);
        _changed.Add(rel);
        return $"Wrote {rel} ({content?.Length ?? 0} chars).";
    }

    public string Grep(string pattern, string path = ".")
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return "pattern required";

        var root = WorkspacePaths.Resolve(_work, string.IsNullOrWhiteSpace(path) ? "." : path);
        var hits = new List<string>();
        IEnumerable<string> files = Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            : File.Exists(root) ? [root] : [];

        foreach (var file in files)
        {
            var rel = WorkspacePaths.Relativize(_work, file);
            if (WorkspacePaths.IsIgnored(rel))
                continue;
            string[] lines;
            try { lines = File.ReadAllLines(file); }
            catch { continue; }

            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    hits.Add($"{rel}:{i + 1}: {Trim(lines[i], 200)}");
                    if (hits.Count >= 40)
                        return string.Join('\n', hits) + "\n… truncated …";
                }
            }
        }

        return hits.Count == 0 ? "No matches." : string.Join('\n', hits);
    }

    public static string ToolResultJson(string name, string result)
    {
        return JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["tool"] = name,
            ["result"] = result.Length > 20_000 ? result[..20_000] + "…" : result
        });
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
