using System.Diagnostics;
using System.Text;
using AutoCoder.Abstractions;

namespace AutoCoder.Core.Sandbox;

/// <summary>Runs allowlisted tools in a local work directory (no Docker required).</summary>
public sealed class LocalSandboxRunner : ISandboxRunner
{
    private readonly HashSet<string> _allowlist;
    private string? _workDirectory;

    public LocalSandboxRunner(IEnumerable<string>? commandAllowlist = null)
    {
        _allowlist = new HashSet<string>(
            commandAllowlist ?? ["git", "dotnet"],
            StringComparer.OrdinalIgnoreCase);
    }

    public Task ProvisionAsync(SandboxSpec spec, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(spec.WorkDirectory);
        _workDirectory = spec.WorkDirectory;

        foreach (var exe in spec.CommandAllowlist)
            _allowlist.Add(exe);

        Console.WriteLine($"[sandbox] Local workspace ready: {spec.WorkDirectory}");
        return Task.CompletedTask;
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

        var psi = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = _workDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        var stdOut = new StringBuilder();
        var stdErr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdOut.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stdErr.AppendLine(e.Data); };

        Console.WriteLine($"[sandbox] {executable} {string.Join(' ', args)}");
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancellationToken);

        return new SandboxCommandResult
        {
            ExitCode = process.ExitCode,
            StdOut = stdOut.ToString(),
            StdErr = stdErr.ToString()
        };
    }

    public Task DisposeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
