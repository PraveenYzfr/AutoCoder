using AutoCoder.Abstractions.Config;

namespace AutoCoder.Core.Runs;

/// <summary>
/// Path contract for the VM:
/// app file I/O uses the container path; the host path is only for sandbox <c>docker run -v</c>.
/// </summary>
public static class RunWorkspace
{
    public const string DefaultContainerRoot = "/app/runs";

    /// <summary>
    /// Directory for AutoCoder's own I/O (run.log, plan, dashboard, lease).
    /// Never returns <c>AUTOCODER_HOST_WORKSPACE_ROOT</c>.
    /// </summary>
    public static string AppRoot(AutoCoderOptions? options = null)
    {
        var container = Env("AUTOCODER_CONTAINER_WORKSPACE_ROOT");
        if (!string.IsNullOrWhiteSpace(container))
            return Ensure(container);

        var configured = options?.Webhooks.ArtifactsDirectory?.Trim();
        var host = HostRoot();
        if (!string.IsNullOrWhiteSpace(configured) && IsSamePath(configured, host))
            return Ensure(DefaultContainerRoot);

        if (!string.IsNullOrWhiteSpace(configured))
            return Ensure(configured);

        var local = Path.GetFullPath("runs");
        return Directory.Exists(local) ? local : Ensure(configured ?? "runs");
    }

    public static string? HostRoot() =>
        Env("AUTOCODER_HOST_WORKSPACE_ROOT") ?? Env("AUTOCODER_HOST_RUNS_PATH");

    private static string? Env(string name)
    {
        var v = Environment.GetEnvironmentVariable(name)?.Trim();
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    private static bool IsSamePath(string a, string? b)
    {
        if (string.IsNullOrWhiteSpace(b))
            return false;
        return string.Equals(
            a.Replace('\\', '/').TrimEnd('/'),
            b.Replace('\\', '/').TrimEnd('/'),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string Ensure(string path)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(full);
        return full;
    }
}
