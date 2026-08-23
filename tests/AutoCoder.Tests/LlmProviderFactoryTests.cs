using AutoCoder.Abstractions.Config;
using AutoCoder.Core.DryRun;
using AutoCoder.Core.Llm;

namespace AutoCoder.Tests;

/// <summary>
/// Item 10e in CLAUDE-AUTOCODER.md: outside dry-run, a missing provider key must throw at startup
/// instead of silently substituting the heuristic stub (which fabricates a plan and lets a real PR
/// open from it).
/// </summary>
[Collection(ProcessEnvStateCollection.Name)]
public sealed class LlmProviderFactoryTests : IDisposable
{
    private readonly string? _deepseek;
    private readonly string? _groq;
    private readonly string? _anthropic;
    private readonly string? _openai;
    private readonly string? _gemini;
    private readonly string? _google;
    private readonly string? _costlyOverride;

    public LlmProviderFactoryTests()
    {
        _deepseek = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
        _groq = Environment.GetEnvironmentVariable("GROQ_API_KEY");
        _anthropic = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        _openai = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        _gemini = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        _google = Environment.GetEnvironmentVariable("GOOGLE_API_KEY");
        _costlyOverride = Environment.GetEnvironmentVariable("AUTOCODER_COSTLY_PROVIDER");
        ClearAllKeys();
    }

    [Fact]
    public void Missing_key_throws_outside_dry_run()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => LlmProviderFactory.CreateNamed("deepseek", "deepseek-v4-flash", dryRun: false));
        Assert.Contains("DEEPSEEK_API_KEY", ex.Message);
        Assert.Contains("--dry-run", ex.Message);
    }

    [Fact]
    public void Missing_key_falls_back_to_heuristic_in_dry_run_only()
    {
        var provider = LlmProviderFactory.CreateNamed("deepseek", "deepseek-v4-flash", dryRun: true);
        Assert.IsType<HeuristicLlmProvider>(provider);
    }

    [Fact]
    public void Present_key_never_returns_the_heuristic_stub()
    {
        Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", "test-key");
        var provider = LlmProviderFactory.CreateNamed("deepseek", "deepseek-v4-flash", dryRun: false);
        Assert.IsNotType<HeuristicLlmProvider>(provider);
    }

    [Fact]
    public void No_keys_anywhere_throws_outside_dry_run_instead_of_fabricating_a_plan()
    {
        var options = RoutedOptions();
        Assert.Throws<InvalidOperationException>(() => LlmProviderFactory.Create(options, "default", dryRun: false));
    }

    [Fact]
    public void No_keys_anywhere_is_fine_in_dry_run()
    {
        var options = RoutedOptions();
        var provider = LlmProviderFactory.Create(options, "default", dryRun: true);
        Assert.NotNull(provider);
    }

    [Fact]
    public void Routed_construction_succeeds_when_both_live_providers_have_keys()
    {
        Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", "test-deepseek");
        Environment.SetEnvironmentVariable("GROQ_API_KEY", "test-groq");

        var provider = LlmProviderFactory.Create(RoutedOptions(), "default", dryRun: false);

        Assert.IsType<RoutedLlmProvider>(provider);
    }

    [Fact]
    public void Explicitly_configured_costly_provider_without_a_key_swaps_to_an_available_one()
    {
        // costly: anthropic configured, but only DEEPSEEK_API_KEY is set on this deploy.
        Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", "test-deepseek");
        var options = RoutedOptions();
        options.Agents["default"].Costly = new AgentOptions { Type = "anthropic", Model = "claude-sonnet-5" };

        var provider = LlmProviderFactory.Create(options, "default", dryRun: false);

        Assert.IsType<RoutedLlmProvider>(provider);
    }

    private static AutoCoderOptions RoutedOptions()
    {
        var options = new AutoCoderOptions();
        options.Agents["default"] = new AgentOptions { Type = "routed" };
        return options;
    }

    private static void ClearAllKeys()
    {
        Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", null);
        Environment.SetEnvironmentVariable("GROQ_API_KEY", null);
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
        Environment.SetEnvironmentVariable("GEMINI_API_KEY", null);
        Environment.SetEnvironmentVariable("GOOGLE_API_KEY", null);
        Environment.SetEnvironmentVariable("AUTOCODER_COSTLY_PROVIDER", null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", _deepseek);
        Environment.SetEnvironmentVariable("GROQ_API_KEY", _groq);
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", _anthropic);
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", _openai);
        Environment.SetEnvironmentVariable("GEMINI_API_KEY", _gemini);
        Environment.SetEnvironmentVariable("GOOGLE_API_KEY", _google);
        Environment.SetEnvironmentVariable("AUTOCODER_COSTLY_PROVIDER", _costlyOverride);
    }
}
