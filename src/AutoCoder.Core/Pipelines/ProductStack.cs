using System.Text.Json;
using AutoCoder.Abstractions.Config;

namespace AutoCoder.Core.Pipelines;

/// <summary>Detects how to verify a cloned product repo. Skip-as-pass is not allowed when gates are required.</summary>
internal static class ProductStack
{
    public static bool HasDotnet(string work) =>
        Find(work, "*.sln") is not null
        || Find(work, "*.slnx") is not null
        || Find(work, "*.csproj") is not null;

    public static bool HasNode(string work) => PackageJson(work) is not null;

    public static bool HasPython(string work) =>
        File.Exists(Path.Combine(work, "pyproject.toml"))
        || File.Exists(Path.Combine(work, "requirements.txt"))
        || File.Exists(Path.Combine(work, "setup.py"))
        || Find(work, "test_*.py") is not null
        || Directory.Exists(Path.Combine(work, "tests"));

    public static bool Any(string work) => HasDotnet(work) || HasNode(work) || HasPython(work);

    public static string? PackageJson(string work) => FindNamed(work, "package.json");

    public static bool HasNpmTestScript(string work)
    {
        var path = PackageJson(work);
        if (path is null)
            return false;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("scripts", out var scripts)
                || scripts.ValueKind != JsonValueKind.Object)
                return false;
            return scripts.TryGetProperty("test", out var test)
                   && test.ValueKind == JsonValueKind.String
                   && !string.IsNullOrWhiteSpace(test.GetString());
        }
        catch
        {
            return false;
        }
    }

    public static string? DotnetBuildTarget(string work) =>
        Find(work, "*.sln") ?? Find(work, "*.slnx") ?? Find(work, "*.csproj");

    public static string? DotnetTestTarget(string work) =>
        Find(work, "*.sln")
        ?? Find(work, "*.slnx")
        ?? Directory.EnumerateFiles(work, "*Test*.csproj", SearchOption.AllDirectories)
            .FirstOrDefault(p => !Ignored(p));

    public static bool HasPythonTests(string work) =>
        Directory.Exists(Path.Combine(work, "tests"))
        || Find(work, "test_*.py") is not null
        || Find(work, "*_test.py") is not null
        || File.Exists(Path.Combine(work, "pytest.ini"));

    public static string Rel(string work, string full) => Path.GetRelativePath(work, full).Replace('\\', '/');

    private static string? FindNamed(string work, string fileName) =>
        Directory.EnumerateFiles(work, fileName, SearchOption.AllDirectories)
            .FirstOrDefault(p => !Ignored(p));

    private static string? Find(string work, string pattern) =>
        Directory.EnumerateFiles(work, pattern, SearchOption.AllDirectories)
            .FirstOrDefault(p => !Ignored(p));

    private static bool Ignored(string path)
    {
        var n = path.Replace('\\', '/');
        return n.Contains("/node_modules/", StringComparison.OrdinalIgnoreCase)
               || n.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
               || n.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
               || n.Contains("/.git/", StringComparison.OrdinalIgnoreCase);
    }
}

internal static class PipelineGates
{
    public static PipelineOptions For(AutoCoderOptions options, string pipelineName)
    {
        if (!string.IsNullOrWhiteSpace(pipelineName)
            && options.Pipelines.TryGetValue(pipelineName, out var named)
            && named is not null)
            return named;
        if (options.Pipelines.TryGetValue("fix-bug", out var fb) && fb is not null)
            return fb;
        return new PipelineOptions();
    }
}
