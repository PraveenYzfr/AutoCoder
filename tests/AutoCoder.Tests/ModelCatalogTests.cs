using AutoCoder.Core.Llm;

namespace AutoCoder.Tests;

public sealed class ModelCatalogTests
{
    [Fact]
    public void IsKnown_matches_provider_and_model_case_insensitively()
    {
        var catalog = new List<CatalogProvider>
        {
            new("deepseek", [new CatalogModel("deepseek-v4-pro")], null)
        };
        Assert.True(ModelCatalog.IsKnown(catalog, "DeepSeek", "DEEPSEEK-V4-PRO"));
        Assert.False(ModelCatalog.IsKnown(catalog, "deepseek", "deepseek-v4-flash"));
        Assert.False(ModelCatalog.IsKnown(catalog, "groq", "deepseek-v4-pro"));
    }

    [Fact]
    public void EnsureCurrentOptions_inserts_the_active_model_when_the_live_list_omits_it()
    {
        var catalog = new List<CatalogProvider>
        {
            new("deepseek", [new CatalogModel("deepseek-v4-flash")], null)
        };
        var roles = new List<RoleEffective> { new("planning", "deepseek", "deepseek-v4-pro", "config") };

        var result = ModelCatalog.EnsureCurrentOptions(catalog, roles);

        var deepseek = result.Single(p => p.Name == "deepseek");
        Assert.Contains(deepseek.Models, m => m.Id == "deepseek-v4-pro");
        Assert.Contains(deepseek.Models, m => m.Id == "deepseek-v4-flash");
    }

    [Fact]
    public void EnsureCurrentOptions_does_not_duplicate_a_model_already_present()
    {
        var catalog = new List<CatalogProvider>
        {
            new("deepseek", [new CatalogModel("deepseek-v4-pro")], null)
        };
        var roles = new List<RoleEffective> { new("planning", "deepseek", "deepseek-v4-pro", "config") };

        var result = ModelCatalog.EnsureCurrentOptions(catalog, roles);

        Assert.Single(result.Single(p => p.Name == "deepseek").Models);
    }

    [Fact]
    public void EnsureCurrentOptions_adds_a_provider_missing_from_the_live_catalog_entirely()
    {
        var catalog = new List<CatalogProvider>
        {
            new("deepseek", [], "no API key")
        };
        var roles = new List<RoleEffective> { new("planning", "anthropic", "claude-sonnet-5", "config") };

        var result = ModelCatalog.EnsureCurrentOptions(catalog, roles);

        var anthropic = result.Single(p => p.Name == "anthropic");
        Assert.Contains(anthropic.Models, m => m.Id == "claude-sonnet-5");
    }

    [Theory]
    [InlineData("whisper-large-v3", false)]
    [InlineData("text-embedding-3-small", false)]
    [InlineData("dall-e-3", false)]
    [InlineData("deepseek-v4-pro", true)]
    [InlineData("claude-sonnet-5", true)]
    public void IsChatModel_filters_non_chat_models(string id, bool expected)
    {
        Assert.Equal(expected, ModelCatalog.IsChatModel(id));
    }

    [Theory]
    // DeepSeek Sept 2026
    [InlineData("deepseek", "deepseek-v4-flash", true)]
    [InlineData("deepseek", "deepseek-v4-pro", true)]
    [InlineData("deepseek", "deepseek-v4-flash-vision-exp", false)]
    // Groq Sept 2026
    [InlineData("groq", "openai/gpt-oss-120b", true)]
    [InlineData("groq", "openai/gpt-oss-20b", true)]
    [InlineData("groq", "qwen/qwen3.8-27b", true)]
    [InlineData("groq", "whisper-large-v3", false)]
    [InlineData("groq", "canopylabs/orpheus-v1-english", false)]
    [InlineData("groq", "meta-llama/llama-prompt-guard-2-22m", false)]
    [InlineData("groq", "openai/gpt-oss-safeguard-20b", false)]
    [InlineData("groq", "groq/compound", false)]
    // OpenAI Sept 2026 — GPT-5.3+ aliases only (flagship is 5.6 Sol/Terra/Luna)
    [InlineData("openai", "gpt-5.6-sol", true)]
    [InlineData("openai", "gpt-5.6-terra", true)]
    [InlineData("openai", "gpt-5.6-luna", true)]
    [InlineData("openai", "gpt-5.5", true)]
    [InlineData("openai", "gpt-5.3-codex", true)]
    [InlineData("openai", "gpt-5.4-mini", true)]
    [InlineData("openai", "gpt-5.2", false)]
    [InlineData("openai", "gpt-5", false)]
    [InlineData("openai", "gpt-5.4-2026-03-05", false)]
    [InlineData("openai", "gpt-4o", false)]
    [InlineData("openai", "gpt-4.1-mini", false)]
    [InlineData("openai", "gpt-realtime-2.1", false)]
    [InlineData("openai", "gpt-image-2", false)]
    [InlineData("openai", "sora-2", false)]
    [InlineData("openai", "o3", false)]
    [InlineData("openai", "o1-pro-2025-03-19", false)]
    // Anthropic Sept 2026
    [InlineData("anthropic", "claude-fable-5-1", true)]
    [InlineData("anthropic", "claude-opus-5", true)]
    [InlineData("anthropic", "claude-sonnet-5", true)]
    [InlineData("anthropic", "claude-haiku-4-5-20251001", true)]
    [InlineData("anthropic", "claude-mythos-5-1", false)]
    // Gemini Sept 2026 — text Flash/Pro only
    [InlineData("gemini", "gemini-3.8-flash", true)]
    [InlineData("gemini", "gemini-3.7-flash", true)]
    [InlineData("gemini", "gemini-3.5-flash-lite", true)]
    [InlineData("gemini", "gemini-3.1-pro-preview", true)]
    [InlineData("gemini", "gemini-2.5-pro", true)]
    [InlineData("gemini", "gemini-3.1-flash-image", false)]
    [InlineData("gemini", "gemini-3.1-flash-tts-preview", false)]
    [InlineData("gemini", "gemini-3.1-flash-live-preview", false)]
    [InlineData("gemini", "gemini-embedding-001", false)]
    [InlineData("gemini", "veo-3.1-generate-preview", false)]
    [InlineData("gemini", "gemma-4-31b-it", true)]
    public void IsChatModel_provider_aware_sept_2026_inventory(string provider, string id, bool expected)
    {
        Assert.Equal(expected, ModelCatalog.IsChatModel(id, provider));
    }
}
