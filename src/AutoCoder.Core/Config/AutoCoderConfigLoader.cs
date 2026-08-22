using System.Text.RegularExpressions;
using AutoCoder.Abstractions.Config;
using AutoCoder.Core.Resilience;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace AutoCoder.Core.Config;

public static class AutoCoderConfigLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static AutoCoderOptions Load(string? path = null)
    {
        var resolved = ResolvePath(path);
        AutoCoderOptions options;

        if (resolved is not null && File.Exists(resolved))
        {
            var raw = File.ReadAllText(resolved);
            raw = ExpandEnvRefs(raw);
            options = Deserializer.Deserialize<AutoCoderOptions>(raw) ?? new AutoCoderOptions();
        }
        else
        {
            options = new AutoCoderOptions();
        }

        ApplyEnvOverlays(options);
        ProjectCatalog.ApplyRuntimeOverlays(options);
        TransientRetry.Configure(options.Resilience);
        return options;
    }

    public static string? ResolvePath(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            var explicitPath = Path.GetFullPath(path);
            if (File.Exists(explicitPath))
                return explicitPath;

            // Also try from process cwd and common parents (dotnet run uses project dir).
            foreach (var root in SearchRoots())
            {
                var candidate = Path.GetFullPath(Path.Combine(root, path));
                if (File.Exists(candidate))
                    return candidate;
            }

            return explicitPath;
        }

        foreach (var root in SearchRoots())
        {
            foreach (var relative in new[] { "autocoder.yml", Path.Combine("config", "autocoder.yml"), Path.Combine("docs", "config-example.yml") })
            {
                var candidate = Path.Combine(root, relative);
                if (File.Exists(candidate))
                    return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    private static IEnumerable<string> SearchRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                if (seen.Add(dir.FullName))
                    yield return dir.FullName;
                dir = dir.Parent;
            }
        }
    }

    private static void ApplyEnvOverlays(AutoCoderOptions options)
    {
        var enabled = Environment.GetEnvironmentVariable("AUTOCODER_WEBHOOKS_ENABLED");
        if (bool.TryParse(enabled, out var webhooksEnabled))
            options.Webhooks.Enabled = webhooksEnabled;

        var mode = Environment.GetEnvironmentVariable("AUTOCODER_TRIGGERS_MODE");
        if (!string.IsNullOrWhiteSpace(mode))
            options.Triggers.Mode = mode.Trim();

        var dryRun = Environment.GetEnvironmentVariable("AUTOCODER_WEBHOOKS_DRY_RUN");
        if (bool.TryParse(dryRun, out var webhookDryRun))
            options.Webhooks.DryRun = webhookDryRun;

        var port = Environment.GetEnvironmentVariable("AUTOCODER_WEBHOOKS_PORT");
        if (int.TryParse(port, out var listenPort))
            options.Webhooks.ListenPort = listenPort;

        var poll = Environment.GetEnvironmentVariable("AUTOCODER_JIRA_POLL");
        if (bool.TryParse(poll, out var pollEnabled))
            options.Poll.Enabled = pollEnabled;

        var sandbox = Environment.GetEnvironmentVariable("AUTOCODER_SANDBOX");
        if (!string.IsNullOrWhiteSpace(sandbox))
            options.Sandbox.Type = sandbox.Trim();

        var concurrent = Environment.GetEnvironmentVariable("AUTOCODER_MAX_CONCURRENT_RUNS");
        if (int.TryParse(concurrent, out var maxConcurrent))
            options.Limits.MaxConcurrentRuns = maxConcurrent;

        var retryAttempts = Environment.GetEnvironmentVariable("AUTOCODER_RETRY_MAX_ATTEMPTS");
        if (int.TryParse(retryAttempts, out var maxAttempts))
            options.Resilience.MaxAttempts = maxAttempts;

        var retryDelay = Environment.GetEnvironmentVariable("AUTOCODER_RETRY_BASE_DELAY_MS");
        if (int.TryParse(retryDelay, out var baseDelayMs))
            options.Resilience.BaseDelayMs = baseDelayMs;

        ApplyLlmOverlays(options);
    }

    /// <summary>
    /// Routed cheap/costly is the default. AUTOCODER_LLM_PROVIDER no longer overwrites
    /// a routed agent to a single backend (that used to make DeepSeek eat planning too).
    /// Set AUTOCODER_LLM_FORCE_SINGLE=true to force one provider for every role.
    /// </summary>
    private static void ApplyLlmOverlays(AutoCoderOptions options)
    {
        if (options.Agents.Count == 0)
            return;

        var llm = Environment.GetEnvironmentVariable("AUTOCODER_LLM_PROVIDER")?.Trim().ToLowerInvariant();
        var cheapEnv = Environment.GetEnvironmentVariable("AUTOCODER_CHEAP_PROVIDER")?.Trim().ToLowerInvariant();
        var costlyEnv = Environment.GetEnvironmentVariable("AUTOCODER_COSTLY_PROVIDER")?.Trim().ToLowerInvariant();
        var forceSingle = bool.TryParse(Environment.GetEnvironmentVariable("AUTOCODER_LLM_FORCE_SINGLE"), out var fs) && fs;

        foreach (var agent in options.Agents.Values)
        {
            var routed = IsRouted(agent);
            if (!string.IsNullOrWhiteSpace(llm) && llm is not "routed" and not "tiered")
            {
                if (forceSingle || !routed)
                {
                    ApplySingleProvider(agent, llm);
                    continue;
                }

                if (IsCheapProvider(llm))
                    agent.Cheap = MergeSlot(agent.Cheap, llm);
                else
                    agent.Costly = MergeSlot(agent.Costly, llm);
                agent.Type = "routed";
            }

            if (!string.IsNullOrWhiteSpace(cheapEnv))
            {
                agent.Type = "routed";
                agent.Cheap = MergeSlot(agent.Cheap, cheapEnv);
            }

            if (!string.IsNullOrWhiteSpace(costlyEnv))
            {
                agent.Type = "routed";
                agent.Costly = MergeSlot(agent.Costly, costlyEnv);
                if (costlyEnv is "deepseek" &&
                    (string.IsNullOrWhiteSpace(agent.Costly.Model) || agent.Costly.Model.Contains("flash", StringComparison.OrdinalIgnoreCase)))
                    agent.Costly.Model = "deepseek-v4-pro";
            }
        }
    }

    private static bool IsRouted(AgentOptions agent)
    {
        var type = (agent.Type ?? "").Trim().ToLowerInvariant();
        return type is "routed" or "tiered" or "" || agent.Cheap is not null || agent.Costly is not null;
    }

    private static bool IsCheapProvider(string type) => type is "deepseek" or "groq";

    private static void ApplySingleProvider(AgentOptions agent, string type)
    {
        agent.Type = type;
        agent.Cheap = null;
        agent.Costly = null;
        ApplySlotDefaults(agent, type);
    }

    private static AgentOptions MergeSlot(AgentOptions? existing, string type)
    {
        var slot = existing ?? new AgentOptions();
        var typeChanged = !string.Equals(slot.Type, type, StringComparison.OrdinalIgnoreCase);
        slot.Type = type;
        if (typeChanged || string.IsNullOrWhiteSpace(slot.Model))
            ApplySlotDefaults(slot, type);
        else if (type == "deepseek" && string.IsNullOrWhiteSpace(slot.Endpoint))
            slot.Endpoint = "https://api.deepseek.com/v1";
        else if (type == "groq" && string.IsNullOrWhiteSpace(slot.Endpoint))
            slot.Endpoint = "https://api.groq.com/openai/v1";
        return slot;
    }

    private static void ApplySlotDefaults(AgentOptions slot, string type)
    {
        switch (type)
        {
            case "deepseek":
                slot.Model = "deepseek-v4-flash";
                slot.Endpoint = "https://api.deepseek.com/v1";
                break;
            case "groq":
                slot.Model = "llama-3.1-8b-instant";
                slot.Endpoint = "https://api.groq.com/openai/v1";
                break;
            case "openai":
                slot.Model = "gpt-4o";
                slot.Endpoint = "https://api.openai.com/v1";
                break;
            case "anthropic":
            case "claude":
                slot.Model = "claude-sonnet-5";
                break;
            case "gemini":
            case "google":
                slot.Model = "gemini-flash-latest";
                break;
        }
    }

    private static string ExpandEnvRefs(string yaml)
    {
        return Regex.Replace(yaml, @"\$\{([A-Za-z_][A-Za-z0-9_]*)\}", m =>
        {
            var name = m.Groups[1].Value;
            return Environment.GetEnvironmentVariable(name) ?? "";
        });
    }
}
