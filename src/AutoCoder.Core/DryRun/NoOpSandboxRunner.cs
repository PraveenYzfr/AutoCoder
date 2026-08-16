using AutoCoder.Abstractions;

namespace AutoCoder.Core.DryRun;

/// <summary>No-op sandbox for dry-run — never executes commands.</summary>
public sealed class NoOpSandboxRunner : ISandboxRunner
{
    public Task ProvisionAsync(SandboxSpec spec, CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"[dry-run] Would provision sandbox at '{spec.WorkDirectory}' image={spec.Image ?? "(local)"}");
        return Task.CompletedTask;
    }

    public Task<SandboxCommandResult> RunAllowlistedAsync(
        string executable,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"[dry-run] Would run allowlisted: {executable} {string.Join(' ', args)}");
        return Task.FromResult(new SandboxCommandResult { ExitCode = 0, StdOut = "(skipped in dry-run)" });
    }

    public Task DisposeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
