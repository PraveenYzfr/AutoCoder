using AutoCoder.Abstractions;
using AutoCoder.Abstractions.Config;
using AutoCoder.Core.DryRun;

namespace AutoCoder.Core.Llm;

public static class LlmProviderFactory
{
    /// <summary>
    /// <paramref name="dryRun"/> controls what happens when a provider's API key is missing:
    /// dry-run substitutes <see cref="HeuristicLlmProvider"/> (no network, for local testing only);
    /// live runs throw at construction time instead of silently fabricating a plan and opening a
    /// real PR from it (item 10e in CLAUDE-AUTOCODER.md).
    /// </summary>
    public static ILlmProvider Create(AutoCoderOptions options, string? agentName = null, bool dryRun = false)
    {
        var agent = GetAgent(options, agentName);
        var type = (agent.Type ?? "routed").Trim().ToLowerInvariant();
        if (type is "routed" or "tiered" || agent.Cheap is not null || agent.Costly is not null)
            return CreateRouted(agent, dryRun);

        return CreateBackend(agent, type, dryRun);
    }

    public static AgentOptions GetAgent(AutoCoderOptions options, string? agentName = null)
    {
        agentName ??= options.Projects.Values.Select(p => p.Agent).FirstOrDefault(a => !string.IsNullOrWhiteSpace(a))
            ?? "default";
        if (options.Agents.TryGetValue(agentName, out var agent) && agent is not null)
            return agent;
        return new AgentOptions { Type = "routed" };
    }

    public static string ResolveType(AutoCoderOptions options, string? agentName = null) =>
        (GetAgent(options, agentName).Type ?? "routed").Trim().ToLowerInvariant();

    /// <summary>Coding loop uses the cheap tier (DeepSeek) unless AUTOCODER_CODING_TIER=costly.</summary>
    public static string ResolveCodingType(AutoCoderOptions options, string? agentName = null)
    {
        var agent = GetAgent(options, agentName);
        var coding = Environment.GetEnvironmentVariable("AUTOCODER_CODING_TIER")?.Trim().ToLowerInvariant();
        var slot = string.Equals(coding, "costly", StringComparison.OrdinalIgnoreCase)
            ? agent.Costly
            : agent.Cheap;
        if (ModelOverrideStore.TryGet("coding", out var over) && over is not null)
            return over.Provider.Trim().ToLowerInvariant();
        if (slot is not null && !string.IsNullOrWhiteSpace(slot.Type))
            return slot.Type.Trim().ToLowerInvariant();
        var type = ResolveType(options, agentName);
        return type is "routed" or "tiered" ? "deepseek" : type;
    }

    public static string ResolveCodingModel(AutoCoderOptions options, string? agentName = null)
    {
        var agent = GetAgent(options, agentName);
        var coding = Environment.GetEnvironmentVariable("AUTOCODER_CODING_TIER")?.Trim().ToLowerInvariant();
        var slot = string.Equals(coding, "costly", StringComparison.OrdinalIgnoreCase)
            ? agent.Costly
            : agent.Cheap;
        if (ModelOverrideStore.TryGet("coding", out var over) && over is not null)
            return SanitizeModel(over.Provider, over.Model);
        var model = slot?.Model
            ?? Environment.GetEnvironmentVariable("AUTOCODER_AGENT_MODEL")
            ?? DeepSeekModels.Flash;
        var type = ResolveCodingType(options, agentName);
        return type switch
        {
            "deepseek" => DeepSeekModels.Sanitize(model),
            "groq" => GroqModels.Sanitize(model),
            _ => model
        };
    }

    private static ILlmProvider CreateRouted(AgentOptions agent, bool dryRun)
    {
        var cheapSlot = agent.Cheap ?? new AgentOptions { Type = "deepseek", Model = DeepSeekModels.Flash };
        var costlySlot = agent.Costly ?? DefaultCostlySlot();

        // A slot picked from explicit config can still be missing its key on this deploy (e.g.
        // costly: anthropic configured, but ANTHROPIC_API_KEY unset here) — swap to another
        // *available* type rather than fail outright. This is a startup-time choice of which real
        // provider to use, not the mid-run resilience fallback below.
        if (!dryRun && !HasKey(costlySlot.Type))
        {
            var swap = CostlyFallbacks(costlySlot.Type).FirstOrDefault();
            if (swap is not null)
            {
                Console.WriteLine(
                    $"[llm] No key for configured costly '{costlySlot.Type}'; using '{swap.Type}' instead.");
                costlySlot = swap;
            }
        }

        var cheap = CreateBackend(cheapSlot, cheapSlot.Type, dryRun);
        var costly = CreateBackend(costlySlot, costlySlot.Type, dryRun);
        if (costly is HeuristicLlmProvider)
        {
            // Only reachable in dry-run (live runs throw above instead of yielding a heuristic).
            Console.WriteLine("[llm] No costly key (DeepSeek/Groq/Anthropic); planning will use cheap DeepSeek.");
            costly = cheap;
        }

        Console.WriteLine(
            $"[llm] Routed: cheap={cheapSlot.Type}/{cheapSlot.Model ?? "(default)"} "
            + $"costly={costlySlot.Type}/{costlySlot.Model ?? "(default)"} "
            + "(summarize/coding=cheap, planning/thinking=costly)");

        cheap = WithTierFallback("cheap", cheapSlot.Type, DeepSeekModels.Flash, GroqModels.Fast, cheap, dryRun);
        costly = WithTierFallback("costly", costlySlot.Type, DeepSeekModels.Pro, GroqModels.Quality, costly, dryRun);

        var overrides = ModelOverrideStore.Load();
        return new RoutedLlmProvider(cheap, costly, agent.RoleTiers, overrides.Roles, (p, m) => CreateNamed(p, m, dryRun));
    }

    /// <summary>
    /// Item 10b in CLAUDE-AUTOCODER.md: fall back within the cost tier only — deepseek &lt;-&gt; groq —
    /// never automatically escalating to Anthropic/OpenAI/Gemini (the benchmark budget, not a safety
    /// net). If the primary is one of those benchmark providers, or the other live provider has no
    /// key configured, there is nothing to fall back to and the provider is returned unwrapped.
    /// </summary>
    private static ILlmProvider WithTierFallback(
        string tier, string primaryType, string deepSeekModel, string groqModel, ILlmProvider primary, bool dryRun)
    {
        if (dryRun || primary is HeuristicLlmProvider)
            return primary;

        var type = primaryType.Trim().ToLowerInvariant();
        var fallbackType = type switch
        {
            "deepseek" when HasKey("groq") => "groq",
            "groq" when HasKey("deepseek") => "deepseek",
            _ => null
        };
        if (fallbackType is null)
            return primary;

        var fallbackModel = fallbackType == "groq" ? groqModel : deepSeekModel;
        var fallback = CreateNamed(fallbackType, fallbackModel, dryRun);
        Console.WriteLine($"[llm] {tier} tier: {type} -> {fallbackType} fallback wired ({fallbackModel}).");
        return new FallbackLlmProvider(tier, [(type, primary), (fallbackType, fallback)]);
    }

    public static ILlmProvider CreateNamed(string type, string? model, bool dryRun = false)
    {
        var slot = new AgentOptions
        {
            Type = type,
            Model = model,
            Endpoint = type switch
            {
                "deepseek" => "https://api.deepseek.com/v1",
                "groq" => GroqModels.BaseUrl,
                "openai" => "https://api.openai.com/v1",
                _ => null
            }
        };
        return CreateBackend(slot, type, dryRun);
    }

    private static string SanitizeModel(string type, string model) => type.Trim().ToLowerInvariant() switch
    {
        "deepseek" => DeepSeekModels.Sanitize(model),
        "groq" => GroqModels.Sanitize(model),
        _ => model
    };

    private static AgentOptions DefaultCostlySlot()
    {
        var forced = Environment.GetEnvironmentVariable("AUTOCODER_COSTLY_PROVIDER")?.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(forced))
            return CostlySlotFor(forced);
        if (HasKey("deepseek"))
            return CostlySlotFor("deepseek");
        if (HasKey("groq"))
            return CostlySlotFor("groq");
        if (HasKey("anthropic"))
            return CostlySlotFor("anthropic");
        if (HasKey("openai"))
            return CostlySlotFor("openai");
        if (HasKey("gemini"))
            return CostlySlotFor("gemini");
        return CostlySlotFor("deepseek");
    }

    private static IEnumerable<AgentOptions> CostlyFallbacks(string? alreadyTried)
    {
        var tried = (alreadyTried ?? "").Trim().ToLowerInvariant();
        foreach (var type in new[] { "deepseek", "groq", "anthropic", "openai", "gemini" })
        {
            if (type == tried || !HasKey(type))
                continue;
            yield return CostlySlotFor(type);
        }
    }

    private static AgentOptions CostlySlotFor(string type) => type switch
    {
        "openai" => new AgentOptions { Type = "openai", Model = "gpt-4o" },
        "gemini" or "google" => new AgentOptions { Type = "gemini", Model = "gemini-flash-latest" },
        "deepseek" => new AgentOptions { Type = "deepseek", Model = DeepSeekModels.Pro },
        "groq" => new AgentOptions { Type = "groq", Model = GroqModels.Quality, Endpoint = GroqModels.BaseUrl },
        _ => new AgentOptions { Type = "anthropic", Model = "claude-sonnet-5" }
    };

    private static bool HasKey(string type) => type switch
    {
        "deepseek" => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY")),
        "groq" => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GROQ_API_KEY")),
        "openai" => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY")),
        "anthropic" or "claude" => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")),
        "gemini" or "google" => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GEMINI_API_KEY"))
                                || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GOOGLE_API_KEY")),
        _ => false
    };

    private static ILlmProvider CreateBackend(AgentOptions agent, string? typeHint, bool dryRun = false)
    {
        var type = (typeHint ?? agent.Type ?? "deepseek").Trim().ToLowerInvariant();
        if (type is "routed" or "tiered" or "")
            type = "deepseek";
        return type switch
        {
            "gemini" or "google" => CreateGemini(agent, dryRun),
            "deepseek" => CreateDeepSeek(agent, dryRun),
            "groq" => CreateGroq(agent, dryRun),
            "openai" => CreateOpenAi(agent, dryRun),
            "anthropic" or "claude" => CreateAnthropic(agent, dryRun),
            "heuristic" or "stub" or "none" => new HeuristicLlmProvider(),
            _ => CreateGeminiOrFallback(agent, type, dryRun)
        };
    }

    public static LlmRoutingInfo Describe(AutoCoderOptions options, string? agentName = null)
    {
        var agent = GetAgent(options, agentName);
        var cheap = agent.Cheap ?? new AgentOptions { Type = "deepseek", Model = DeepSeekModels.Flash };
        var costly = agent.Costly ?? DefaultCostlySlot();
        return new LlmRoutingInfo(
            (agent.Type ?? "routed").Trim().ToLowerInvariant(),
            (cheap.Type ?? "deepseek").Trim().ToLowerInvariant(),
            cheap.Model ?? DeepSeekModels.Flash,
            (costly.Type ?? "anthropic").Trim().ToLowerInvariant(),
            costly.Model ?? "claude-sonnet-5",
            ResolveCodingType(options, agentName),
            ResolveCodingModel(options, agentName));
    }

    /// <summary>
    /// Dry-run substitutes the heuristic stub (no network); live runs throw instead of silently
    /// fabricating a plan and opening a real PR from it (item 10e in CLAUDE-AUTOCODER.md).
    /// </summary>
    private static ILlmProvider MissingKey(string envVar, string providerLabel, bool dryRun)
    {
        if (dryRun)
        {
            Console.WriteLine($"[llm] {envVar} not set; falling back to heuristic (dry-run only).");
            return new HeuristicLlmProvider();
        }

        throw new InvalidOperationException(
            $"{envVar} is required for the {providerLabel} provider outside --dry-run. AutoCoder will "
            + "not silently substitute a fabricated plan for a real PR — see CLAUDE-AUTOCODER.md item 10e.");
    }

    private static ILlmProvider CreateDeepSeek(AgentOptions agent, bool dryRun = false)
    {
        var key = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
            return MissingKey("DEEPSEEK_API_KEY", "DeepSeek", dryRun);

        var roleModels = RoleModels(agent);
        var model = DeepSeekModels.Sanitize(agent.Model ?? roleModels.GetValueOrDefault("cheap") ?? DeepSeekModels.Flash);
        var baseUrl = string.IsNullOrWhiteSpace(agent.Endpoint)
            ? "https://api.deepseek.com/v1"
            : agent.Endpoint.TrimEnd('/');
        Console.WriteLine($"[llm] DeepSeek model '{model}'.");
        return new OpenAiCompatibleLlmProvider(key, baseUrl, model, "deepseek", roleModels);
    }

    private static ILlmProvider CreateGroq(AgentOptions agent, bool dryRun = false)
    {
        var key = Environment.GetEnvironmentVariable("GROQ_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
            return MissingKey("GROQ_API_KEY", "Groq", dryRun);

        var roleModels = RoleModels(agent);
        var model = GroqModels.Sanitize(agent.Model ?? roleModels.GetValueOrDefault("cheap") ?? GroqModels.Fast);
        var baseUrl = string.IsNullOrWhiteSpace(agent.Endpoint)
            ? GroqModels.BaseUrl
            : agent.Endpoint.TrimEnd('/');
        Console.WriteLine($"[llm] Groq model '{model}' (label=groq, not openai).");
        return new OpenAiCompatibleLlmProvider(key, baseUrl, model, "groq", roleModels);
    }

    private static ILlmProvider CreateOpenAi(AgentOptions agent, bool dryRun = false)
    {
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
            return MissingKey("OPENAI_API_KEY", "OpenAI", dryRun);

        var roleModels = RoleModels(agent);
        var model = agent.Model ?? roleModels.GetValueOrDefault("planning") ?? "gpt-4o";
        var baseUrl = string.IsNullOrWhiteSpace(agent.Endpoint)
            ? "https://api.openai.com/v1"
            : agent.Endpoint.TrimEnd('/');
        Console.WriteLine($"[llm] OpenAI model '{model}'.");
        return new OpenAiCompatibleLlmProvider(key, baseUrl, model, "openai", roleModels);
    }

    private static ILlmProvider CreateAnthropic(AgentOptions agent, bool dryRun = false)
    {
        var key = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
            return MissingKey("ANTHROPIC_API_KEY", "Anthropic", dryRun);

        var roleModels = RoleModels(agent);
        var model = agent.Model ?? "claude-sonnet-5";
        Console.WriteLine($"[llm] Anthropic model '{model}'.");
        return new AnthropicLlmProvider(key, model, roleModels);
    }

    private static Dictionary<string, string> RoleModels(AgentOptions agent)
    {
        var roleModels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (role, model) in agent.Models)
        {
            if (!string.IsNullOrWhiteSpace(model.Model))
                roleModels[role] = model.Model;
        }
        return roleModels;
    }

    private static ILlmProvider CreateGeminiOrFallback(AgentOptions agent, string type, bool dryRun = false)
    {
        var key = Environment.GetEnvironmentVariable("GEMINI_API_KEY")
            ?? Environment.GetEnvironmentVariable("GOOGLE_API_KEY");
        if (!string.IsNullOrWhiteSpace(key) && type is "gemini" or "google")
            return CreateGemini(agent, dryRun);

        if (dryRun)
        {
            Console.WriteLine($"[llm] Provider '{type}' not implemented yet; using heuristic stub.");
            return new HeuristicLlmProvider();
        }

        throw new InvalidOperationException(
            $"Provider '{type}' is not implemented and no fallback key is configured for a live run.");
    }

    private static ILlmProvider CreateGemini(AgentOptions agent, bool dryRun = false)
    {
        var key = Environment.GetEnvironmentVariable("GEMINI_API_KEY")
            ?? Environment.GetEnvironmentVariable("GOOGLE_API_KEY");

        if (string.IsNullOrWhiteSpace(key))
            return MissingKey("GEMINI_API_KEY", "Gemini", dryRun);

        var roleModels = RoleModels(agent);
        var defaultModel = agent.Model
            ?? (roleModels.TryGetValue("planning", out var planning) ? planning : null)
            ?? (roleModels.TryGetValue("primary", out var primary) ? primary : null)
            ?? "gemini-flash-latest";

        Console.WriteLine($"[llm] Gemini model '{defaultModel}'.");
        return new GeminiLlmProvider(key, defaultModel, roleModels);
    }
}

public sealed record LlmRoutingInfo(
    string AgentType,
    string CheapType,
    string CheapModel,
    string CostlyType,
    string CostlyModel,
    string CodingType,
    string CodingModel);
