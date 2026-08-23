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
}
