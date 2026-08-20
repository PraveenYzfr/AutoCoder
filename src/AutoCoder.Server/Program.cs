using System.Text;
using AutoCoder.Abstractions.Config;
using AutoCoder.Core.Config;
using AutoCoder.Core.Llm;
using AutoCoder.Core.Logging;
using AutoCoder.Core.Webhooks;
using AutoCoder.Server;
using Microsoft.Extensions.Logging;

DotEnvLoader.Load();

var configPath = args.SkipWhile(a => a != "--config").Skip(1).FirstOrDefault()
    ?? Environment.GetEnvironmentVariable("AUTOCODER_CONFIG");

var options = AutoCoderConfigLoader.Load(configPath);
var loadedFrom = AutoCoderConfigLoader.ResolvePath(configPath) ?? "(defaults)";

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(o =>
{
    o.IncludeScopes = true;
    o.TimestampFormat = "O";
    o.UseUtcTimestamp = true;
});
builder.WebHost.UseUrls($"http://0.0.0.0:{options.Webhooks.ListenPort}");
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<WebhookRunDispatcher>();
builder.Services.AddHostedService<JiraPoller>();

var app = builder.Build();
RunLog.Configure(app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("AutoCoder"));
var webhookPath = string.IsNullOrWhiteSpace(options.Webhooks.Path)
    ? "/webhook/jira"
    : options.Webhooks.Path;

app.MapGet("/health", (AutoCoderOptions opts) =>
{
    var routing = LlmProviderFactory.Describe(opts);
    return Results.Ok(new
    {
        status = "ok",
        product = "AutoCoder",
        config = loadedFrom,
        triggersMode = opts.Triggers.Mode,
        webhooksEnabled = opts.Webhooks.Enabled,
        webhooksDryRun = opts.Webhooks.DryRun,
        webhookPath = opts.Webhooks.Path,
        llmConfigured = HasEnv("DEEPSEEK_API_KEY")
                     || HasEnv("GROQ_API_KEY")
                     || HasEnv("OPENAI_API_KEY")
                     || HasEnv("ANTHROPIC_API_KEY")
                     || HasEnv("GEMINI_API_KEY")
                     || HasEnv("GOOGLE_API_KEY"),
        sandbox = opts.Sandbox.Type,
        poll = opts.Poll.Enabled,
        llm = routing.AgentType,
        cheap = $"{routing.CheapType}/{routing.CheapModel}",
        costly = $"{routing.CostlyType}/{routing.CostlyModel}",
        coding = $"{routing.CodingType}/{routing.CodingModel}"
    });
});

async Task<IResult> HandleJiraWebhook(HttpRequest request, AutoCoderOptions opts, WebhookRunDispatcher dispatcher, CancellationToken ct)
{
    if (!opts.Webhooks.Enabled)
    {
        return Results.Json(new
        {
            accepted = false,
            skipped = true,
            reason = "Webhooks disabled. Set webhooks.enabled: true or AUTOCODER_WEBHOOKS_ENABLED=true."
        });
    }

    if (!WebhookTriggerFilter.IsWebhookTriggerMode(opts.Triggers))
    {
        return Results.Json(new
        {
            accepted = false,
            skipped = true,
            reason = $"triggers.mode='{opts.Triggers.Mode}' does not include webhook. Use 'webhook' or 'both'."
        });
    }

    var body = await new StreamReader(request.Body).ReadToEndAsync(ct);
    if (!WebhookAuthenticator.Validate(
            opts.Webhooks,
            body,
            Environment.GetEnvironmentVariable(opts.Webhooks.SecretEnv),
            request.Headers["X-Hub-Signature"].FirstOrDefault(),
            request.Headers["X-AutoCoder-Token"].FirstOrDefault(),
            request.Headers["Authorization"].FirstOrDefault(),
            request.Query.TryGetValue("token", out var q) ? q.ToString() : null,
            out var secretError))
    {
        RunLog.Event("webhook.rejected", level: LogLevel.Warning, fields: ("reason", secretError ?? "unauthorized"));
        return Results.Json(new { accepted = false, error = secretError }, statusCode: StatusCodes.Status401Unauthorized);
    }

    if (!JiraWebhookParser.TryParse(body, out var parsed, out var parseError) || parsed is null)
        return Results.Json(new { accepted = false, skipped = true, reason = parseError });

    var decision = WebhookTriggerFilter.Evaluate(opts, parsed.Ticket);
    if (!decision.ShouldRun || decision.Project is null || decision.ProjectName is null)
    {
        return Results.Json(new
        {
            accepted = true,
            skipped = true,
            ticket = parsed.Ticket.Key,
            reason = decision.Reason
        });
    }

    if (!dispatcher.TryEnqueue(parsed.Ticket, decision.Project, decision.ProjectName, out var runId, out var skip))
    {
        return Results.Json(new
        {
            accepted = true,
            skipped = true,
            ticket = parsed.Ticket.Key,
            reason = skip ?? "lease held"
        });
    }

    return Results.Json(new
    {
        accepted = true,
        queued = true,
        ticket = parsed.Ticket.Key,
        project = decision.ProjectName,
        runId,
        dryRun = opts.Webhooks.DryRun,
        note = "Pipeline runs in the background. Jira moves to AgentWorking now, then In Review or Agent Failure when finished."
    }, statusCode: StatusCodes.Status202Accepted);
}

app.MapPost(webhookPath, HandleJiraWebhook);
if (!string.Equals(webhookPath, "/webhook", StringComparison.OrdinalIgnoreCase))
    app.MapPost("/webhook", HandleJiraWebhook);

Console.WriteLine("AutoCoder Server");
Console.WriteLine($"  config:            {loadedFrom}");
Console.WriteLine($"  triggers.mode:     {options.Triggers.Mode}");
Console.WriteLine($"  webhooks.enabled:  {options.Webhooks.Enabled}");
Console.WriteLine($"  webhooks.dry_run:  {options.Webhooks.DryRun}");
Console.WriteLine($"  listen:            http://0.0.0.0:{options.Webhooks.ListenPort}");
Console.WriteLine($"  path:              POST {webhookPath}");
Console.WriteLine("  health:            GET  /health");

app.Run();

static bool HasEnv(string name) =>
    !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name));
