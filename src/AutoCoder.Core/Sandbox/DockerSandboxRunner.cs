using System.Diagnostics;
using System.Text;
using AutoCoder.Abstractions;

namespace AutoCoder.Core.Sandbox;

/// <summary>
/// Runs allowlisted build/test commands in a throwaway container.
/// Clone/git stay on the orchestrator; <c>dotnet build</c> / <c>dotnet test</c> of SimpleApp
/// (the cloned product repo) never execute on the host kernel.
/// </summary>
public sealed class DockerSandboxRunner : ISandboxRunner
{
    private readonly HashSet<string> _allowlist;
    private readonly string _defaultImage;
    private readonly string _memory;
    private readonly int _timeoutMinutes;
    private string? _workDirectory;
    private string? _image;

    public DockerSandboxRunner(
        IEnumerable<string>? commandAllowlist = null,
        string? defaultImage = null,
        string memory = "2g",
        int timeoutMinutes = 30)
    {
        _allowlist = new HashSet<string>(
            commandAllowlist ?? ["git", "dotnet", "npm", "python", "pytest"],
            StringComparer.OrdinalIgnoreCase);
        _defaultImage = string.IsNullOrWhiteSpace(defaultImage)
            ? "mcr.microsoft.com/dotnet/sdk:8.0"
            : defaultImage;
        _memory = string.IsNullOrWhiteSpace(memory) ? "2g" : memory;
        _timeoutMinutes = timeoutMinutes <= 0 ? 30 : timeoutMinutes;
    }

    public static bool IsDockerAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "docker",
                ArgumentList = { "version" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p is null)
                return false;
            if (!p.WaitForExit(8000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return false;
            }
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task ProvisionAsync(SandboxSpec spec, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(spec.WorkDirectory);
        _workDirectory = spec.WorkDirectory;
        _image = string.IsNullOrWhiteSpace(spec.Image) ? _defaultImage : spec.Image;
        foreach (var exe in spec.CommandAllowlist)
            _allowlist.Add(exe);

        Console.WriteLine($"[sandbox] Docker workspace {spec.WorkDirectory} image={_image}");
        await DockerAsync(["pull", _image!], TimeSpan.FromMinutes(5), cancellationToken, throwOnError: false);
    }

    public async Task<SandboxCommandResult> RunAllowlistedAsync(
        string executable,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
    {
        var exeName = Path.GetFileNameWithoutExtension(executable);
        if (!_allowlist.Contains(executable) && !_allowlist.Contains(exeName))
        {
            throw new InvalidOperationException(
                $"Command '{executable}' is not allowlisted. Refusing to run.");
        }

        if (string.IsNullOrWhiteSpace(_workDirectory))
            throw new InvalidOperationException("Sandbox not provisioned.");

        var image = ImageFor(exeName);
        var hostWork = ToHostPath(_workDirectory);
        var nugetHost = NugetHostPath(hostWork);
        Directory.CreateDirectory(nugetHost);
        var network = Environment.GetEnvironmentVariable("AUTOCODER_SANDBOX_NETWORK");
        if (string.IsNullOrWhiteSpace(network))
            network = "bridge";
        var name = $"autocoder-sbx-{Guid.NewGuid():N}"[..28];
        var containerArgs = args.Select(a => a.Replace('\\', '/')).ToList();

        var dockerArgs = new List<string>
        {
            "run", "--rm",
            "--name", name,
            "--network", network,
            "--memory", _memory,
            "--cpus", "2",
            "--security-opt", "no-new-privileges",
            "--mount", $"type=bind,source={hostWork},target=/workspace",
            "--mount", $"type=bind,source={nugetHost},target=/root/.nuget/packages",
            "-w", "/workspace",
            image,
            executable
        };
        dockerArgs.AddRange(containerArgs);

        Console.WriteLine($"[sandbox] docker run {image} {executable} {string.Join(' ', containerArgs)}");
        try
        {
            return await DockerAsync(dockerArgs, TimeSpan.FromMinutes(_timeoutMinutes), cancellationToken, throwOnError: false);
        }
        catch (OperationCanceledException)
        {
            await DockerAsync(["rm", "-f", name], TimeSpan.FromSeconds(20), CancellationToken.None, throwOnError: false);
            throw;
        }
    }

    public Task DisposeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    private string ImageFor(string exe) => exe.ToLowerInvariant() switch
    {
        "npm" or "npx" or "node" => "node:20-bookworm",
        "python" or "pytest" => "python:3.12-bookworm",
        _ => _image ?? _defaultImage
    };

    /// <summary>
    /// When AutoCoder itself runs in a container, bind-mounts must use the host path
    /// (AUTOCODER_HOST_WORKSPACE_ROOT) so sibling build containers see the clone.
    /// </summary>
    public static string ToHostPath(string workDirectory)
    {
        var full = Path.GetFullPath(workDirectory).Replace('\\', '/');
        var hostRoot = Environment.GetEnvironmentVariable("AUTOCODER_HOST_WORKSPACE_ROOT")
                       ?? Environment.GetEnvironmentVariable("AUTOCODER_HOST_RUNS_PATH");
        var containerRoot = Environment.GetEnvironmentVariable("AUTOCODER_CONTAINER_WORKSPACE_ROOT")
                            ?? "/app/runs";
        if (string.IsNullOrWhiteSpace(hostRoot))
            return Path.GetFullPath(workDirectory);

        var prefix = containerRoot.TrimEnd('/').Replace('\\', '/');
        if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return hostRoot.TrimEnd('/') + full[prefix.Length..];
        return Path.GetFullPath(workDirectory);
    }

    /// <summary>NuGet cache under the same host tree Claude allows (<c>/var/lib/autocoder/runs/.nuget</c>).</summary>
    private static string NugetHostPath(string hostWork)
    {
        var hostRoot = Environment.GetEnvironmentVariable("AUTOCODER_HOST_WORKSPACE_ROOT")
                       ?? Environment.GetEnvironmentVariable("AUTOCODER_HOST_RUNS_PATH");
        if (!string.IsNullOrWhiteSpace(hostRoot))
            return Path.Combine(hostRoot, ".nuget", "packages");
        var work = Path.GetFullPath(hostWork);
        var parent = Directory.GetParent(work)?.FullName ?? work;
        return Path.Combine(parent, ".nuget", "packages");
    }

    private static async Task<SandboxCommandResult> DockerAsync(
        IReadOnlyList<string> args,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        bool throwOnError)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "docker",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        var stdOut = new StringBuilder();
        var stdErr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdOut.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stdErr.AppendLine(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
            throw;
        }

        var result = new SandboxCommandResult
        {
            ExitCode = process.ExitCode,
            StdOut = stdOut.ToString(),
            StdErr = stdErr.ToString()
        };
        if (throwOnError && process.ExitCode != 0)
            throw new InvalidOperationException($"docker {args[0]} failed ({process.ExitCode}): {result.StdErr}");
        return result;
    }
}
