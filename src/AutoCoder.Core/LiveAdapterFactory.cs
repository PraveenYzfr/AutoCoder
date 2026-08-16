using AutoCoder.Abstractions;
using AutoCoder.Abstractions.Config;
using AutoCoder.Core.Approval;
using AutoCoder.Core.Auth;
using AutoCoder.Core.DryRun;
using AutoCoder.Core.GitHub;
using AutoCoder.Core.Sandbox;

namespace AutoCoder.Core;

public static class LiveAdapterFactory
{
    public static (ISandboxRunner Sandbox, IRepoHost RepoHost, IApprovalGate Gate) Create(
        AutoCoderOptions options,
        bool dryRun,
        bool autoApprove)
    {
        if (dryRun)
        {
            return (
                new NoOpSandboxRunner(),
                new FakeRepoHost(options.Repos.Values.Select(r => r.Url)),
                autoApprove ? new DryRunApprovalGate() : new ConsoleApprovalGate(autoApprove));
        }

        var allowlist = options.Repos.Values
            .Select(r => r.Url)
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        IGitCredentialProvider credentials = GitCredentialProviderFactory.Create(options);
        ISandboxRunner sandbox = CreateSandbox(options);
        IRepoHost repoHost = new GitHubRepoHost(allowlist, credentials);
        IApprovalGate gate = new ConsoleApprovalGate(autoApprove);
        return (sandbox, repoHost, gate);
    }

    public static ISandboxRunner CreateSandbox(AutoCoderOptions options)
    {
        var commands = options.Sandbox.CommandAllowlist.Count > 0
            ? options.Sandbox.CommandAllowlist
            : ["git", "dotnet", "npm", "python", "pytest"];
        var type = options.Sandbox.Type?.Trim().ToLowerInvariant() ?? "docker";
        var requireDocker = string.Equals(
            Environment.GetEnvironmentVariable("AUTOCODER_REQUIRE_DOCKER"), "true", StringComparison.OrdinalIgnoreCase);

        if (type is "docker" or "container")
        {
            if (DockerSandboxRunner.IsDockerAvailable())
            {
                Console.WriteLine("[sandbox] Using throwaway Docker containers for SimpleApp build/test.");
                return new DockerSandboxRunner(
                    commands,
                    options.Sandbox.DefaultImage,
                    options.Sandbox.Memory,
                    options.Sandbox.MaxRuntimeMinutes);
            }

            if (requireDocker)
                throw new InvalidOperationException(
                    "AUTOCODER_REQUIRE_DOCKER=true but docker is not available. Install Docker Engine.");

            Console.WriteLine("[sandbox] Docker not available — laptop fallback: local process (not for the shared VM).");
        }

        return new LocalSandboxRunner(commands);
    }
}
