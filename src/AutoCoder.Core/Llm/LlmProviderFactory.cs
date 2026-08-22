using AutoCoder.Abstractions;
using AutoCoder.Abstractions.Config;
using AutoCoder.Core.DryRun;

namespace AutoCoder.Core.Llm;

public static class LlmProviderFactory
{
    public static ILlmProvider Create(AutoCoderOptions options, string? agentName = null)
    {
        var agent = GetAgent(options, agentName);
        var type = (agent.Type ?? "routed").Trim().ToLowerInvariant();
        if (type is "routed" or "tiered" || agent.Cheap is not null || agent.Costly is not null)
            return CreateRouted(agent);

        return CreateBackend(agent, type);
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

    private static ILlmProvider CreateRouted(AgentOptions agent)
    {
        var cheapSlot = agent.Cheap ?? new AgentOptions { Type = "deepseek", Model = DeepSeekModels.Flash };
        var costlySlot = agent.Costly ?? DefaultCostlySlot();
        var cheap = CreateBackend(cheapSlot, cheapSlot.Type);
        var costly = CreateBackend(costlySlot, costlySlot.Type);
        if (costly is HeuristicLlmProvider)
        {
            foreach (var fallback in CostlyFallbacks(costlySlot.Type))
            {
                var candidate = CreateBackend(fallback, fallback.Type);
                if (candidate is not HeuristicLlmProvider)
                {
                    costly = candidate;
                    costlySlot = fallback;
                    break;
                }
            }
        }

        if (costly is HeuristicLlmProvider)
        {
            Console.WriteLine("[llm] No costly key (DeepSeek/Groq/Anthropic); planning will use cheap DeepSeek.");
            costly = cheap;
        }

        Console.WriteLine(
            $"[llm] Routed: cheap={cheapSlot.Type}/{cheapSlot.Model ?? "(default)"} "
            + $"costly={costlySlot.Type}/{costlySlot.Model ?? "(default)"} "
            + "(summarize/coding=cheap, planning/thinking=costly)");
        return new RoutedLlmProvider(cheap, costly, agent.RoleTiers);
    }

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

    private static ILlmProvider CreateBackend(AgentOptions agent, string? typeHint)
    {
        var type = (typeHint ?? agent.Type ?? "deepseek").Trim().ToLowerInvariant();
        if (type is "routed" or "tiered" or "")
            type = "deepseek";
        return type switch
        {
            "gemini" or "google" => CreateGemini(agent),
            "deepseek" => CreateDeepSeek(agent),
            "groq" => CreateGroq(agent),
            "openai" => CreateOpenAi(agent),
            "anthropic" or "claude" => CreateAnthropic(agent),
            "heuristic" or "stub" or "none" => new HeuristicLlmProvider(),
            _ => CreateGeminiOrFallback(agent, type)
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

    private static ILlmProvider CreateDeepSeek(AgentOptions agent)
    {
        var key = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
        {
            Console.WriteLine("[llm] DEEPSEEK_API_KEY not set; falling back to heuristic.");
            return new HeuristicLlmProvider();
        }

        var roleModels = RoleModels(agent);
        var model = DeepSeekModels.Sanitize(agent.Model ?? roleModels.GetValueOrDefault("cheap") ?? DeepSeekModels.Flash);
        var baseUrl = string.IsNullOrWhiteSpace(agent.Endpoint)
            ? "https://api.deepseek.com/v1"
            : agent.Endpoint.TrimEnd('/');
        Console.WriteLine($"[llm] DeepSeek model '{model}'.");
        return new OpenAiCompatibleLlmProvider(key, baseUrl, model, "deepseek", roleModels);
    }

    private static ILlmProvider CreateGroq(AgentOptions agent)
    {
        var key = Environment.GetEnvironmentVariable("GROQ_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
        {
            Console.WriteLine("[llm] GROQ_API_KEY not set; falling back to heuristic.");
            return new HeuristicLlmProvider();
        }

        var roleModels = RoleModels(agent);
        var model = GroqModels.Sanitize(agent.Model ?? roleModels.GetValueOrDefault("cheap") ?? GroqModels.Fast);
        var baseUrl = string.IsNullOrWhiteSpace(agent.Endpoint)
            ? GroqModels.BaseUrl
            : agent.Endpoint.TrimEnd('/');
        Console.WriteLine($"[llm] Groq model '{model}' (label=groq, not openai).");
        return new OpenAiCompatibleLlmProvider(key, baseUrl, model, "groq", roleModels);
    }

    private static ILlmProvider CreateOpenAi(AgentOptions agent)
    {
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
        {
            Console.WriteLine("[llm] OPENAI_API_KEY not set; falling back to heuristic.");
            return new HeuristicLlmProvider();
        }

        var roleModels = RoleModels(agent);
        var model = agent.Model ?? roleModels.GetValueOrDefault("planning") ?? "gpt-4o";
        var baseUrl = string.IsNullOrWhiteSpace(agent.Endpoint)
            ? "https://api.openai.com/v1"
            : agent.Endpoint.TrimEnd('/');
        Console.WriteLine($"[llm] OpenAI model '{model}'.");
        return new OpenAiCompatibleLlmProvider(key, baseUrl, model, "openai", roleModels);
    }

    private static ILlmProvider CreateAnthropic(AgentOptions agent)
    {
        var key = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
        {
            Console.WriteLine("[llm] ANTHROPIC_API_KEY not set; falling back to heuristic.");
            return new HeuristicLlmProvider();
        }

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

    private static ILlmProvider CreateGeminiOrFallback(AgentOptions agent, string type)
    {
        var key = Environment.GetEnvironmentVariable("GEMINI_API_KEY")
            ?? Environment.GetEnvironmentVariable("GOOGLE_API_KEY");
        if (!string.IsNullOrWhiteSpace(key) && type is "gemini" or "google")
            return CreateGemini(agent);

        Console.WriteLine($"[llm] Provider '{type}' not implemented yet; using heuristic stub.");
        return new HeuristicLlmProvider();
    }

    private static ILlmProvider CreateGemini(AgentOptions agent)
    {
        var key = Environment.GetEnvironmentVariable("GEMINI_API_KEY")
            ?? Environment.GetEnvironmentVariable("GOOGLE_API_KEY");

        if (string.IsNullOrWhiteSpace(key))
        {
            Console.WriteLine("[llm] GEMINI_API_KEY not set; falling back to heuristic planner.");
            return new HeuristicLlmProvider();
        }

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
