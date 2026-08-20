using AutoCoder.Abstractions.Config;
using AutoCoder.Core.Pipelines;

namespace AutoCoder.Tests;

public sealed class ProductStackTests : IDisposable
{
    private readonly string _work;

    public ProductStackTests()
    {
        _work = Path.Combine(Path.GetTempPath(), "autocoder-stack", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_work);
    }

    [Fact]
    public void Detects_node_and_missing_test_script()
    {
        File.WriteAllText(Path.Combine(_work, "package.json"), """
            { "name": "simpleapp", "scripts": { "start": "node server.js" } }
            """);
        Assert.True(ProductStack.HasNode(_work));
        Assert.False(ProductStack.HasNpmTestScript(_work));
        Assert.False(ProductStack.HasDotnet(_work));
    }

    [Fact]
    public void Detects_npm_test_script()
    {
        File.WriteAllText(Path.Combine(_work, "package.json"), """
            { "scripts": { "test": "node --test" } }
            """);
        Assert.True(ProductStack.HasNpmTestScript(_work));
    }

    [Fact]
    public void Ignores_package_json_under_node_modules()
    {
        var nested = Path.Combine(_work, "node_modules", "left-pad");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "package.json"), """{ "scripts": { "test": "true" } }""");
        Assert.False(ProductStack.HasNode(_work));
    }

    [Fact]
    public void Detects_dotnet_and_python()
    {
        File.WriteAllText(Path.Combine(_work, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(_work, "pyproject.toml"), "[project]\nname='x'\n");
        Directory.CreateDirectory(Path.Combine(_work, "tests"));
        Assert.True(ProductStack.HasDotnet(_work));
        Assert.True(ProductStack.HasPython(_work));
        Assert.True(ProductStack.HasPythonTests(_work));
    }

    [Fact]
    public void Pipeline_gates_default_require_tests()
    {
        var gates = PipelineGates.For(new AutoCoderOptions(), "fix-bug");
        Assert.True(gates.RequireBuild);
        Assert.True(gates.RequireTests);
    }

    [Fact]
    public void Pipeline_gates_read_named_pipeline()
    {
        var options = new AutoCoderOptions();
        options.Pipelines["fix-bug"] = new PipelineOptions { RequireTests = false, RequireBuild = true };
        var gates = PipelineGates.For(options, "fix-bug");
        Assert.True(gates.RequireBuild);
        Assert.False(gates.RequireTests);
    }

    public void Dispose()
    {
        try { Directory.Delete(_work, recursive: true); } catch { /* ignore */ }
    }
}
