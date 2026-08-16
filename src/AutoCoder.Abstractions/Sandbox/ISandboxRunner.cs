namespace AutoCoder.Abstractions;

public sealed class SandboxSpec
{
    public required string WorkDirectory { get; init; }
    public string? Image { get; init; }
    public IReadOnlyList<string> CommandAllowlist { get; init; } = ["dotnet", "git"];
}

public sealed class SandboxCommandResult
{
    public int ExitCode { get; init; }
    public string StdOut { get; init; } = "";
    public string StdErr { get; init; } = "";
}

/// <summary>
/// Isolated execution. Implementations must refuse to run ticket text as shell
/// and only execute allowlisted tool binaries with structured args.
/// </summary>
public interface ISandboxRunner
{
    Task ProvisionAsync(SandboxSpec spec, CancellationToken cancellationToken = default);

    Task<SandboxCommandResult> RunAllowlistedAsync(
        string executable,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default);

    Task DisposeAsync(CancellationToken cancellationToken = default);
}
