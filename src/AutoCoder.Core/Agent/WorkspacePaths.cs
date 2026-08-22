namespace AutoCoder.Core.Agent;

internal static class WorkspacePaths
{
    public static string Resolve(string workDirectory, string relative)
    {
        var root = Path.GetFullPath(workDirectory);
        var rel = (relative ?? ".").Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        if (rel.Contains("..", StringComparison.Ordinal))
            throw new InvalidOperationException("Path traversal is not allowed.");

        var full = Path.GetFullPath(Path.Combine(root, rel));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Path is outside the workspace.");

        return full;
    }

    public static string Relativize(string workDirectory, string fullPath)
    {
        var root = Path.GetFullPath(workDirectory);
        var full = Path.GetFullPath(fullPath);
        return Path.GetRelativePath(root, full).Replace('\\', '/');
    }

    public static bool IsIgnored(string relative)
    {
        var p = relative.Replace('\\', '/');
        return p.StartsWith(".git/", StringComparison.OrdinalIgnoreCase)
               || p.Contains("/.git/", StringComparison.OrdinalIgnoreCase)
               || p.StartsWith(".autocoder/", StringComparison.OrdinalIgnoreCase)
               || p.Contains("/.autocoder/", StringComparison.OrdinalIgnoreCase)
               || p.StartsWith("bin/", StringComparison.OrdinalIgnoreCase)
               || p.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
               || p.StartsWith("obj/", StringComparison.OrdinalIgnoreCase)
               || p.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
               || p.Contains("node_modules/", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsProductFile(string relative)
    {
        var p = relative.Replace('\\', '/');
        if (p.StartsWith(".autocoder/", StringComparison.OrdinalIgnoreCase))
            return false;
        if (IsIgnored(p))
            return false;
        return true;
    }
}
