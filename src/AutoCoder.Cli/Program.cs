using System.CommandLine;
using AutoCoder.Abstractions;
using AutoCoder.Core;
using AutoCoder.Core.Config;
using AutoCoder.Core.DryRun;
using AutoCoder.Core.Jira;
using AutoCoder.Core.Llm;
using AutoCoder.Core.Logging;
using AutoCoder.Core.Pipelines;
using Microsoft.Extensions.Logging;

DotEnvLoader.Load();

using var loggerFactory = LoggerFactory.Create(b =>
{
    b.AddSimpleConsole(o =>
    {
        o.TimestampFormat = "HH:mm:ss ";
        o.SingleLine = true;
    });
    b.SetMinimumLevel(LogLevel.Information);
});
RunLog.Configure(loggerFactory.CreateLogger("AutoCoder"));

var configOption = new Option<FileInfo?>(
    name: "--config",
    description: "Path to autocoder.yml");

var artifactsOption = new Option<DirectoryInfo>(
    name: "--artifacts",
    description: "Directory for plan/result/decisions output",
    getDefaultValue: () => new DirectoryInfo("runs"));

var projectOption = new Option<string?>(
    name: "--project",
    description: "Project name from autocoder.yml (optional if only one project)");

var yesOption = new Option<bool>(
    name: "--yes",
    description: "Auto-approve the plan (skip interactive prompt)");

var liveOption = new Option<bool>(
    name: "--live",
    description: "Use real local git + GitHub PR (requires GITHUB_TOKEN + GITHUB_REPO_URL)");

// --- dry-run ---
var ticketFileOption = new Option<FileInfo>(name: "--ticket", description: "Path to ticket JSON") { IsRequired = true };
var dryRunCommand = new Command("dry-run", "Local demo from ticket JSON (fake sandbox/PR unless --live)");
dryRunCommand.AddOption(ticketFileOption);
dryRunCommand.AddOption(artifactsOption);
dryRunCommand.AddOption(configOption);
dryRunCommand.AddOption(projectOption);
dryRunCommand.AddOption(yesOption);
dryRunCommand.AddOption(liveOption);
dryRunCommand.SetHandler(async (FileInfo ticket, DirectoryInfo artifacts, FileInfo? config, string? project, bool yes, bool live) =>
{
    if (!ticket.Exists)
    {
        Console.Error.WriteLine($"Ticket file not found: {ticket.FullName}");
        Environment.ExitCode = 1;
        return;
    }

    var options = AutoCoderConfigLoader.Load(config?.FullName);
    ProjectCatalog.ApplyRuntimeOverlays(options);
    if (!live)
        EnsureDryRunPlaceholders(options);

    if (live)
    {
        try { RequireLiveConfig(options); }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            Environment.ExitCode = 1;
            return;
        }
    }

    artifacts.Create();
    ITicketSource ticketSource = CompositeTicketSource.WithJiraWriteback(
        new SampleJsonTicketSource(ticket.FullName),
        CompositeTicketSource.FirstJiraUrl(options),
        live);
    await RunPipelineAsync(options, ticketSource, artifacts.FullName, "from-file", project, dryRun: !live, yes, "dry-run");
}, ticketFileOption, artifactsOption, configOption, projectOption, yesOption, liveOption);

// --- run (Jira ticket key) ---
var ticketKeyOption = new Option<string>(name: "--ticket", description: "Jira issue key, e.g. AC-101") { IsRequired = true };
var runCommand = new Command("run", "Fetch Jira ticket by key; use --live for real GitHub PR");
runCommand.AddOption(ticketKeyOption);
runCommand.AddOption(artifactsOption);
runCommand.AddOption(configOption);
runCommand.AddOption(projectOption);
runCommand.AddOption(yesOption);
runCommand.AddOption(liveOption);
runCommand.SetHandler(async (string ticketKey, DirectoryInfo artifacts, FileInfo? config, string? project, bool yes, bool live) =>
{
    var options = AutoCoderConfigLoader.Load(config?.FullName);
    ProjectCatalog.ApplyRuntimeOverlays(options);

    ResolvedProject resolved;
    try
    {
        resolved = ProjectCatalog.Resolve(options, ticket: null, projectName: project);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.Message);
        Environment.ExitCode = 1;
        return;
    }

    if (live)
    {
        try { RequireLiveConfig(options); }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            Environment.ExitCode = 1;
            return;
        }
    }

    artifacts.Create();
    Console.WriteLine($"Jira base URL: {resolved.JiraBaseUrl}");
    Console.WriteLine($"Target repo:   {resolved.Repo.Url}");
    Console.WriteLine($"Ticket:        {ticketKey}");
    Console.WriteLine($"Browse:        {ProjectCatalog.BrowseUrl(resolved.JiraBaseUrl, ticketKey)}");
    Console.WriteLine($"Mode:          {(live ? "LIVE (clone/commit/PR)" : "dry-run adapters")}");

    ITicketSource ticketSource = new JiraTicketSource(resolved.JiraBaseUrl);
    await RunPipelineAsync(
        options,
        ticketSource,
        artifacts.FullName,
        ticketKey,
        resolved.ProjectName,
        dryRun: !live,
        yes,
        ticketKey.ToLowerInvariant());
}, ticketKeyOption, artifactsOption, configOption, projectOption, yesOption, liveOption);

var root = new RootCommand("AutoCoder — ticket → plan → code → PR");
root.AddCommand(dryRunCommand);
root.AddCommand(runCommand);
return await root.InvokeAsync(args);

static async Task RunPipelineAsync(
    AutoCoder.Abstractions.Config.AutoCoderOptions options,
    ITicketSource ticketSource,
    string artifactsDir,
    string ticketKey,
    string? projectName,
    bool dryRun,
    bool autoApprove,
    string slug)
{
    var agentName = projectName is not null && options.Projects.TryGetValue(projectName, out var p)
        ? p.Agent
        : null;
    ILlmProvider llm = LlmProviderFactory.Create(options, agentName);
    var (sandbox, repoHost, gate) = LiveAdapterFactory.Create(options, dryRun, autoApprove || dryRun);

    var pipeline = new FixBugPipeline(options, ticketSource, llm, gate, sandbox, repoHost);
    var runId = PipelineRunner.NewRunId(slug);
    var context = new PipelineContext
    {
        RunId = runId,
        PipelineName = pipeline.Name,
        DryRun = dryRun,
        ArtifactsDirectory = artifactsDir,
        ProjectName = projectName,
        Items =
        {
            ["ticketKey"] = ticketKey,
            ["projectName"] = projectName
        }
    };

    await new PipelineRunner().RunAsync(pipeline, context, options);
    Console.WriteLine();
    Console.WriteLine(dryRun ? "Dry-run complete." : "Live run complete.");
    Console.WriteLine($"Artifacts: {Path.Combine(artifactsDir, runId)}");
    if (context.PullRequest is not null)
        Console.WriteLine($"PR: {context.PullRequest.Url}");
}

static void RequireLiveConfig(AutoCoder.Abstractions.Config.AutoCoderOptions options)
{
    ProjectCatalog.ApplyRuntimeOverlays(options);
    if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GITHUB_TOKEN")))
        throw new InvalidOperationException("GITHUB_TOKEN is required for --live.");

    var repo = options.Repos.Values.Select(r => r.Url).FirstOrDefault(u => !string.IsNullOrWhiteSpace(u));
    if (string.IsNullOrWhiteSpace(repo) || repo.Contains("your-org", StringComparison.OrdinalIgnoreCase) || repo.Contains("example/", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Set GITHUB_REPO_URL (or repos.*.url) to your real GitHub repo for --live.");
}

static void EnsureDryRunPlaceholders(AutoCoder.Abstractions.Config.AutoCoderOptions options)
{
    foreach (var tracker in options.Trackers.Values)
    {
        if (string.IsNullOrWhiteSpace(tracker.Url) || tracker.Url.Contains("${") || tracker.Url.Contains("your-org"))
            tracker.Url = "https://example.atlassian.net";
    }

    foreach (var repo in options.Repos.Values)
    {
        if (string.IsNullOrWhiteSpace(repo.Url) || repo.Url.Contains("${") || repo.Url.Contains("your-org"))
        {
            repo.Url = "https://github.com/example/sample-api";
            repo.Allowlist = ["github.com/example/sample-api"];
        }
    }
}
