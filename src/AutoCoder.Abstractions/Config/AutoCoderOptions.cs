namespace AutoCoder.Abstractions.Config;

/// <summary>Root AutoCoder configuration (YAML + env overlays).</summary>
public sealed class AutoCoderOptions
{
    public TriggersOptions Triggers { get; set; } = new();
    public WebhooksOptions Webhooks { get; set; } = new();
    public Dictionary<string, AgentOptions> Agents { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, RepoOptions> Repos { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, TrackerOptions> Trackers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ProjectOptions> Projects { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public SandboxOptions Sandbox { get; set; } = new();
    public PollOptions Poll { get; set; } = new();
    public Dictionary<string, string> Secrets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class TriggersOptions
{
    /// <summary>cli | webhook | both</summary>
    public string Mode { get; set; } = "cli";
}

public sealed class WebhooksOptions
{
    /// <summary>Master switch. Also overridable via AUTOCODER_WEBHOOKS_ENABLED.</summary>
    public bool Enabled { get; set; }

    public int ListenPort { get; set; } = 8081;

    public string Path { get; set; } = "/webhook/jira";

    /// <summary>When true, reject requests if secret is missing/mismatched.</summary>
    public bool RequireSecret { get; set; } = true;

    /// <summary>Env var name holding the shared secret (HMAC or plain token).</summary>
    public string SecretEnv { get; set; } = "JIRA_WEBHOOK_SECRET";

    /// <summary>
    /// When true (default for now), webhook runs use dry-run adapters
    /// (no real Jira/GitHub/LLM calls). Set false when live adapters exist.
    /// </summary>
    public bool DryRun { get; set; } = true;

    public string ArtifactsDirectory { get; set; } = "runs";
}

public sealed class AgentOptions
{
    public string Type { get; set; } = "routed";
    public string? Endpoint { get; set; }
    public string? ApiVersion { get; set; }
    public string? Model { get; set; }
    public Dictionary<string, ModelOptions> Models { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Cheap tier (Flash/DeepSeek): summarize, scout, coding volume.</summary>
    public AgentOptions? Cheap { get; set; }
    /// <summary>Costly tier (Pro/Sonnet/GPT-4o): planning, thinking, decisions.</summary>
    public AgentOptions? Costly { get; set; }
    /// <summary>planning/thinking → costly, summarize/coding → cheap. Override per role.</summary>
    public Dictionary<string, string> RoleTiers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ModelOptions
{
    public string Model { get; set; } = "";
    public string? Deployment { get; set; }
    public int? MaxTokens { get; set; }
}

public sealed class RepoOptions
{
    public string Type { get; set; } = "github";
    public string Url { get; set; } = "";
    public string DefaultBranch { get; set; } = "main";
    public List<string> Allowlist { get; set; } = [];
}

public sealed class TrackerOptions
{
    public string Type { get; set; } = "jira";
    /// <summary>Jira site base URL, e.g. https://acme.atlassian.net (no trailing path).</summary>
    public string Url { get; set; } = "";
    public string Auth { get; set; } = "jira_token";
    public string? Email { get; set; }
    public List<string> OpenStates { get; set; } = [];
    public string? DoneStatus { get; set; }
    public string? FailedStatus { get; set; }
    public string? NeedsClarificationStatus { get; set; }
}

public sealed class ProjectOptions
{
    public string Agent { get; set; } = "";
    public string Tracker { get; set; } = "";
    public List<string> Repos { get; set; } = [];
    public string Pipeline { get; set; } = "fix-bug";
    public string HumanApproval { get; set; } = "required";
    public bool AutoMerge { get; set; }
    public JiraTriggerOptions? JiraTrigger { get; set; }
}

public sealed class JiraTriggerOptions
{
    public ProjectResolutionOptions? ProjectResolution { get; set; }
    public List<string> TriggerStatuses { get; set; } = ["AssignedToAgent"];
    public string? CommentKeyword { get; set; }
    public string? Secret { get; set; }
}

public sealed class ProjectResolutionOptions
{
    public string Strategy { get; set; } = "tag";
    public string Value { get; set; } = "";
}

public sealed class SandboxOptions
{
    /// <summary>local | docker. Live SimpleApp build/test uses docker throwaway containers.</summary>
    public string Type { get; set; } = "docker";
    public string? DefaultImage { get; set; }
    public List<string> CommandAllowlist { get; set; } = ["dotnet", "git"];
    public int MaxRuntimeMinutes { get; set; } = 30;
    public string Memory { get; set; } = "2g";
}

/// <summary>Backup when Jira webhooks miss (VM was deallocated).</summary>
public sealed class PollOptions
{
    public bool Enabled { get; set; }
    public int IntervalSeconds { get; set; } = 300;
    public string Jql { get; set; } = "status = \"AssignedToAgent\"";
}
