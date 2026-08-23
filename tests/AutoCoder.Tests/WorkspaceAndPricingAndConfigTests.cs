using AutoCoder.Abstractions.Config;
using AutoCoder.Core.Agent;
using AutoCoder.Core.Config;
using AutoCoder.Core.Llm;
using AutoCoder.Core.Runs;

namespace AutoCoder.Tests;

[Collection(ProcessEnvStateCollection.Name)]
public sealed class WorkspaceAndPricingAndConfigTests
{
    [Fact]
    public void Path_traversal_is_rejected()
    {
        var work = Path.Combine(Path.GetTempPath(), "autocoder-ws", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            Assert.Throws<InvalidOperationException>(() => WorkspacePaths.Resolve(work, "../secret.env"));
            var ok = WorkspacePaths.Resolve(work, "src/app.js");
            Assert.StartsWith(Path.GetFullPath(work), ok, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Node_modules_are_not_product_files()
    {
        Assert.False(WorkspacePaths.IsProductFile("node_modules/left-pad/index.js"));
        Assert.True(WorkspacePaths.IsProductFile("src/app.js"));
    }

    [Fact]
    public void Pricing_is_zero_for_heuristic_and_positive_for_claude()
    {
        Assert.Equal(0m, LlmPricing.Estimate("heuristic", "none", 10_000, 10_000));
        Assert.True(LlmPricing.Estimate("anthropic", "claude-sonnet-5", 1_000_000, 1_000_000) > 10m);
        Assert.True(LlmPricing.Estimate("deepseek", "deepseek-v4-flash", 1_000_000, 0) < 1m);
    }

    [Fact]
    public void Enterprise_yaml_binds_limits_and_pipeline_gates()
    {
        var path = FindRepoFile(Path.Combine("config", "enterprise.yml"));
        var options = AutoCoderConfigLoader.Load(path);
        Assert.Equal(5m, options.Limits.MaxUsdPerRun);
        Assert.Equal(500_000, options.Limits.MaxTokensPerRun);
        Assert.Equal(40, options.Limits.MaxToolCalls);
        Assert.Equal(2, options.Limits.MaxConcurrentRuns);
        Assert.True(options.Pipelines.TryGetValue("fix-bug", out var pipe));
        Assert.True(pipe.RequireBuild);
        Assert.True(pipe.RequireTests);
        Assert.True(pipe.NeverMerge);
        Assert.False(options.Projects["simpleapp"].AutoMerge);
        Assert.Equal(3, options.Resilience.MaxAttempts);
        Assert.Equal(250, options.Resilience.BaseDelayMs);
        Assert.Equal("deepseek", options.Agents["default"].Costly?.Type);
        Assert.Equal("deepseek-v4-pro", options.Agents["default"].Costly?.Model);
        Assert.Equal("/app/runs", options.Webhooks.ArtifactsDirectory);
    }

    [Fact]
    public void App_io_uses_container_path_never_host()
    {
        var prevC = Environment.GetEnvironmentVariable("AUTOCODER_CONTAINER_WORKSPACE_ROOT");
        var prevH = Environment.GetEnvironmentVariable("AUTOCODER_HOST_WORKSPACE_ROOT");
        var container = Path.Combine(Path.GetTempPath(), "ac-app-runs", Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable("AUTOCODER_CONTAINER_WORKSPACE_ROOT", container);
            Environment.SetEnvironmentVariable("AUTOCODER_HOST_WORKSPACE_ROOT", "/var/lib/autocoder/runs");
            var options = new AutoCoderOptions();
            options.Webhooks.ArtifactsDirectory = "/var/lib/autocoder/runs";
            var root = RunWorkspace.AppRoot(options);
            Assert.Equal(Path.GetFullPath(container), root);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AUTOCODER_CONTAINER_WORKSPACE_ROOT", prevC);
            Environment.SetEnvironmentVariable("AUTOCODER_HOST_WORKSPACE_ROOT", prevH);
            try { Directory.Delete(container, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Anthropic_omits_temperature_on_sonnet_5()
    {
        Assert.False(AnthropicLlmProvider.AcceptsTemperature("claude-sonnet-5"));
        Assert.False(AnthropicLlmProvider.AcceptsTemperature("claude-opus-4-5"));
        Assert.True(AnthropicLlmProvider.AcceptsTemperature("claude-3-5-sonnet-20241022"));
    }

    private static string FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException(relative);
    }
}
