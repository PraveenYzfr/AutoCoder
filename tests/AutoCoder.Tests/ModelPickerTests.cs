using AutoCoder.Abstractions.Config;
using AutoCoder.Core.Llm;

namespace AutoCoder.Tests;

[Collection(ProcessEnvStateCollection.Name)]
public sealed class ModelPickerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ac-models", Guid.NewGuid().ToString("N"));
    private readonly string? _prev;

    public ModelPickerTests()
    {
        Directory.CreateDirectory(_root);
        _prev = Environment.GetEnvironmentVariable("AUTOCODER_CONTAINER_WORKSPACE_ROOT");
        Environment.SetEnvironmentVariable("AUTOCODER_CONTAINER_WORKSPACE_ROOT", _root);
    }

    [Fact]
    public void Chat_filter_drops_non_llm_models()
    {
        Assert.False(ModelCatalog.IsChatModel("whisper-large-v3"));
        Assert.False(ModelCatalog.IsChatModel("text-embedding-3-small"));
        Assert.True(ModelCatalog.IsChatModel("deepseek-v4-pro"));
        Assert.True(ModelCatalog.IsChatModel("claude-sonnet-5"));
    }

    [Fact]
    public void Override_persists_and_resets()
    {
        ModelOverrideStore.Set("coding", "groq", "openai/gpt-oss-120b", "test");
        Assert.True(ModelOverrideStore.TryGet("coding", out var over));
        Assert.Equal("groq", over!.Provider);
        Assert.Equal("openai/gpt-oss-120b", over.Model);
        ModelOverrideStore.Reset("coding", "test");
        Assert.False(ModelOverrideStore.TryGet("coding", out _));
    }

    [Fact]
    public void Effective_marks_override_vs_config()
    {
        var options = new AutoCoderOptions();
        options.Agents["default"] = new AgentOptions
        {
            Type = "routed",
            Cheap = new AgentOptions { Type = "deepseek", Model = "deepseek-v4-flash" },
            Costly = new AgentOptions { Type = "deepseek", Model = "deepseek-v4-pro" }
        };
        var before = ModelCatalog.Effective(options).Single(r => r.Role == "planning");
        Assert.Equal("config", before.Source);
        ModelOverrideStore.Set("planning", "groq", "openai/gpt-oss-120b", "test");
        var after = ModelCatalog.Effective(options).Single(r => r.Role == "planning");
        Assert.Equal("override", after.Source);
        Assert.Equal("groq", after.Provider);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("AUTOCODER_CONTAINER_WORKSPACE_ROOT", _prev);
        try { Directory.Delete(_root, true); } catch { /* ignore */ }
    }
}
