using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AutoCoder.Abstractions.Config;
using AutoCoder.Core.Runs;

namespace AutoCoder.Core.Dashboard;

public static class RunCatalog
{
    public static readonly string[] PipelineStages =
    [
        "FetchTicket", "ResolveProject", "ExtractTicket", "ProvisionSandbox",
        "ScoutRepo", "GeneratePlan", "ApprovalGate", "AgenticImplement",
        "Build", "Test", "SecretScan", "CommitAndOpenPr", "WritebackTicket", "PersistRunResult"
    ];

    public static string ResolveRoot(AutoCoderOptions? options = null) =>
        RunWorkspace.AppRoot(options);

    public static IReadOnlyList<RunSummary> List(string root, int take = 50)
    {
        if (!Directory.Exists(root))
            return [];

        return Directory.GetDirectories(root)
            .Select(dir => ReadSummary(dir))
            .Where(s => s is not null)
            .Cast<RunSummary>()
            .OrderByDescending(s => s.StartedUtc ?? DateTime.MinValue)
            .Take(Math.Clamp(take, 1, 200))
            .ToList();
    }

    public static RunDetail? Get(string root, string runId)
    {
        if (string.IsNullOrWhiteSpace(runId) || runId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return null;
        var dir = Path.Combine(root, runId);
        if (!Directory.Exists(dir))
            return null;
        return ReadDetail(dir);
    }

    public static IReadOnlyList<JsonElement> ReadLog(string root, string runId, int skip = 0)
    {
        var path = Path.Combine(root, runId, "run.log");
        if (!File.Exists(path))
            return [];
        return ParseLog(path).Skip(Math.Max(0, skip)).ToList();
    }

    public static RunSummary? Current(string root) =>
        List(root, 20).FirstOrDefault(s => s.Status == "running");

    private static RunSummary? ReadSummary(string dir)
    {
        var events = ParseLog(Path.Combine(dir, "run.log"));
        if (events.Count == 0 && !File.Exists(Path.Combine(dir, "result.md")))
            return null;
        return BuildDetail(Path.GetFileName(dir), dir, events).ToSummary();
    }

    private static RunDetail ReadDetail(string dir) =>
        BuildDetail(Path.GetFileName(dir), dir, ParseLog(Path.Combine(dir, "run.log")));

    private static List<JsonElement> ParseLog(string path)
    {
        var list = new List<JsonElement>();
        if (!File.Exists(path))
            return list;
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                list.Add(doc.RootElement.Clone());
            }
            catch
            {
                // skip a torn last line on a live run
            }
        }

        return list;
    }

    private static RunDetail BuildDetail(string runId, string dir, List<JsonElement> events)
    {
        var first = events.FirstOrDefault();
        var last = events.Count > 0 ? events[^1] : default;
        var started = ParseTs(Str(first, "ts"));
        var ended = HasEvent(events, "run.succeeded") || HasEvent(events, "run.failed")
            ? ParseTs(Str(last, "ts"))
            : null;
        var failedStep = First(events, "run.failed", "step") ?? First(events, "step.failed", "step");
        var status = HasEvent(events, "run.succeeded") ? "succeeded"
            : HasEvent(events, "run.failed") ? "failed"
            : events.Count > 0 ? "running"
            : "unknown";
        if (Bool(first, "dryRun") && status == "succeeded")
            status = "dry-run";

        var stages = PipelineStages.Select(name =>
        {
            var start = events.LastOrDefault(e => Str(e, "event") == "step.started" && Str(e, "step") == name);
            var ok = events.LastOrDefault(e => Str(e, "event") == "step.succeeded" && Str(e, "step") == name);
            var fail = events.LastOrDefault(e => Str(e, "event") == "step.failed" && Str(e, "step") == name);
            var state = fail.ValueKind != JsonValueKind.Undefined ? "failed"
                : ok.ValueKind != JsonValueKind.Undefined ? "done"
                : start.ValueKind != JsonValueKind.Undefined ? "running"
                : "pending";
            var ms = Num(ok, "ms") ?? Num(fail, "ms");
            return new StageState(name, state, ms);
        }).ToList();

        var llm = events.Where(e => Str(e, "event") == "llm.call")
            .GroupBy(e => new
            {
                Role = Str(e, "role") ?? "unknown",
                Tier = Str(e, "tier") ?? "unknown",
                Provider = Str(e, "provider") ?? "unknown",
                Model = Str(e, "model") ?? "unknown"
            })
            .Select(g => new ModelUse(
                g.Key.Role,
                g.Key.Tier,
                g.Key.Provider,
                g.Key.Model,
                g.Count(),
                (int)g.Sum(e => Num(e, "prompt") ?? 0),
                (int)g.Sum(e => Num(e, "completion") ?? 0),
                g.Sum(e => Dec(e, "usd") ?? 0m)))
            .OrderBy(m => RoleOrder(m.Role))
            .ToList();

        var writes = events
            .Where(e => Str(e, "event") == "agent.tool" && Str(e, "tool") == "write_file")
            .Select(e => Str(e, "path"))
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var turns = events.Count(e => Str(e, "event") == "agent.turn");
        var maxTurns = events.Select(e => Num(e, "maxTurns")).LastOrDefault(n => n is > 0);
        var tools = (int?)Num(last, "toolCalls") ?? events.Count(e => Str(e, "event") == "agent.tool");
        var finished = events.Any(e => Str(e, "event") == "agent.tool" && Str(e, "tool") == "finish")
                       || events.Any(e => Str(e, "event") == "agent.finished" && Bool(e, "finished"));
        var agentStarted = events.LastOrDefault(e => Str(e, "event") == "agent.started");

        var resultMd = ReadOptional(dir, "result.md");
        var pr = First(events, "pr.opened", "url") ?? ExtractMd(resultMd, "PR:");
        var journey = BuildJourney(stages, pr);

        return new RunDetail
        {
            RunId = runId,
            Ticket = Str(first, "ticket") ?? ExtractMd(resultMd, "Ticket:"),
            Pipeline = Str(first, "pipeline") ?? "fix-bug",
            Status = status,
            DryRun = Bool(first, "dryRun"),
            StartedUtc = started,
            EndedUtc = ended,
            DurationMs = started is { } s && (ended ?? DateTime.UtcNow) is var e
                ? (e - s).TotalMilliseconds
                : null,
            FailedStep = failedStep,
            LastStep = stages.LastOrDefault(st => st.State is "done" or "running" or "failed")?.Name,
            PrUrl = pr,
            Tokens = (int?)Num(last, "tokens") ?? 0,
            Usd = Dec(last, "usd") ?? 0m,
            ToolCalls = tools,
            Stages = stages,
            Models = llm,
            Coding =             new CodingProgress(
                turns,
                maxTurns is { } mt ? (int)mt : null,
                tools,
                writes.Count,
                finished,
                Str(agentStarted, "provider"),
                Str(agentStarted, "model"),
                writes),
            Plan = ReadOptional(dir, "plan.md"),
            Scout = ReadOptional(dir, "scout.md"),
            TicketBrief = ReadOptional(dir, "ticket-brief.md"),
            Result = resultMd,
            BrowseUrl = null,
            Journey = journey,
            NowLabel = journey.FirstOrDefault(j => j.State == "running")?.Label
                       ?? (pr is not null ? "PR opened" : journey.LastOrDefault(j => j.State is "done" or "failed")?.Label)
        };
    }

    private static readonly (string Label, string[] Steps)[] JourneyMap =
    [
        ("Take Jira ticket", ["FetchTicket", "ResolveProject", "ExtractTicket"]),
        ("Clone repo", ["ProvisionSandbox"]),
        ("Scout", ["ScoutRepo"]),
        ("Plan", ["GeneratePlan", "ApprovalGate"]),
        ("Code", ["AgenticImplement"]),
        ("Build & test", ["Build", "Test"]),
        ("Scan secrets", ["SecretScan"]),
        ("Open PR", ["CommitAndOpenPr"]),
        ("Update Jira", ["WritebackTicket", "PersistRunResult"])
    ];

    private static List<JourneyStep> BuildJourney(List<StageState> stages, string? prUrl)
    {
        var byName = stages.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        return JourneyMap.Select(j =>
        {
            var parts = j.Steps.Select(n => byName.TryGetValue(n, out var s) ? s : new StageState(n, "pending", null)).ToList();
            var state = parts.Any(p => p.State == "failed") ? "failed"
                : parts.All(p => p.State == "done") || (j.Label == "Open PR" && !string.IsNullOrWhiteSpace(prUrl)) ? "done"
                : parts.Any(p => p.State is "running" or "done") ? "running"
                : "pending";
            return new JourneyStep(j.Label, state);
        }).ToList();
    }

    private static int RoleOrder(string role) => role.ToLowerInvariant() switch
    {
        "scout" => 0,
        "planning" or "thinking" or "decision" => 1,
        "coding" => 2,
        "summarize" or "comment" => 3,
        _ => 9
    };

    private static bool HasEvent(List<JsonElement> events, string name) =>
        events.Any(e => Str(e, "event") == name);

    private static string? First(List<JsonElement> events, string name, string field) =>
        events.Where(e => Str(e, "event") == name).Select(e => Str(e, field)).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));

    private static string? Str(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var p))
            return null;
        return p.ValueKind == JsonValueKind.String ? p.GetString() : p.ToString();
    }

    private static bool Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p)
        && p.ValueKind is JsonValueKind.True;

    private static double? Num(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var p))
            return null;
        return p.ValueKind switch
        {
            JsonValueKind.Number => p.GetDouble(),
            JsonValueKind.String when double.TryParse(p.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var n) => n,
            _ => null
        };
    }

    private static decimal? Dec(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var p))
            return null;
        return p.ValueKind switch
        {
            JsonValueKind.Number => p.GetDecimal(),
            JsonValueKind.String when decimal.TryParse(p.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var n) => n,
            _ => null
        };
    }

    private static DateTime? ParseTs(string? ts) =>
        DateTime.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d.ToUniversalTime() : null;

    private static string? ReadOptional(string dir, string file)
    {
        var path = Path.Combine(dir, file);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private static string? ExtractMd(string? md, string label)
    {
        if (string.IsNullOrWhiteSpace(md))
            return null;
        var m = Regex.Match(md, $@"{Regex.Escape(label)}\s*`?(\S+)`?");
        var v = m.Success ? m.Groups[1].Value.Trim('`', '"') : null;
        return string.IsNullOrWhiteSpace(v) || v is "n/a" ? null : v;
    }
}

public sealed record StageState(string Name, string State, double? DurationMs);

public sealed record JourneyStep(string Label, string State);

public sealed record ModelUse(
    string Role, string Tier, string Provider, string Model,
    int Calls, int PromptTokens, int CompletionTokens, decimal Usd);

public sealed record CodingProgress(
    int Turns, int? MaxTurns, int ToolCalls, int FilesWritten, bool Finished,
    string? Provider, string? Model, IReadOnlyList<string> Files);

public sealed class RunSummary
{
    public required string RunId { get; init; }
    public string? Ticket { get; init; }
    public string? Pipeline { get; init; }
    public required string Status { get; init; }
    public bool DryRun { get; init; }
    public DateTime? StartedUtc { get; init; }
    public double? DurationMs { get; init; }
    public string? FailedStep { get; init; }
    public string? LastStep { get; init; }
    public string? PrUrl { get; init; }
    public int Tokens { get; init; }
    public decimal Usd { get; init; }
    public int ToolCalls { get; init; }
    public int FilesWritten { get; init; }
    public bool CodingFinished { get; init; }
    public string? ModelMix { get; init; }
    public string? NowLabel { get; init; }
    public IReadOnlyList<JourneyStep> Journey { get; init; } = [];
}

public sealed class RunDetail
{
    public required string RunId { get; init; }
    public string? Ticket { get; init; }
    public string? Pipeline { get; init; }
    public required string Status { get; init; }
    public bool DryRun { get; init; }
    public DateTime? StartedUtc { get; init; }
    public DateTime? EndedUtc { get; init; }
    public double? DurationMs { get; init; }
    public string? FailedStep { get; init; }
    public string? LastStep { get; init; }
    public string? PrUrl { get; init; }
    public int Tokens { get; init; }
    public decimal Usd { get; init; }
    public int ToolCalls { get; init; }
    public IReadOnlyList<StageState> Stages { get; init; } = [];
    public IReadOnlyList<ModelUse> Models { get; init; } = [];
    public CodingProgress? Coding { get; init; }
    public string? Plan { get; init; }
    public string? Scout { get; init; }
    public string? TicketBrief { get; init; }
    public string? Result { get; init; }
    public string? BrowseUrl { get; init; }
    public string? NowLabel { get; init; }
    public IReadOnlyList<JourneyStep> Journey { get; init; } = [];

    public RunSummary ToSummary() => new()
    {
        RunId = RunId,
        Ticket = Ticket,
        Pipeline = Pipeline,
        Status = Status,
        DryRun = DryRun,
        StartedUtc = StartedUtc,
        DurationMs = DurationMs,
        FailedStep = FailedStep,
        LastStep = LastStep,
        PrUrl = PrUrl,
        Tokens = Tokens,
        Usd = Usd,
        ToolCalls = ToolCalls,
        FilesWritten = Coding?.FilesWritten ?? 0,
        CodingFinished = Coding?.Finished ?? false,
        ModelMix = Mix(),
        NowLabel = NowLabel,
        Journey = Journey
    };

    private string? Mix()
    {
        if (Models.Count == 0)
            return null;
        string Pick(string role) =>
            Models.FirstOrDefault(m => m.Role.Equals(role, StringComparison.OrdinalIgnoreCase)) is { } u
                ? $"{u.Provider}/{u.Model}"
                : "";
        var plan = Pick("planning");
        var code = Pick("coding");
        var scout = Pick("scout");
        var bits = new List<string>();
        if (!string.IsNullOrWhiteSpace(scout)) bits.Add($"scout: {scout}");
        if (!string.IsNullOrWhiteSpace(plan)) bits.Add($"plan: {plan}");
        if (!string.IsNullOrWhiteSpace(code)) bits.Add($"code: {code}");
        return bits.Count == 0 ? null : string.Join(" · ", bits);
    }
}
