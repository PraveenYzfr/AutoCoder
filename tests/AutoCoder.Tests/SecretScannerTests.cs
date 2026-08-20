using AutoCoder.Abstractions;
using AutoCoder.Core.Pipelines;

namespace AutoCoder.Tests;

public sealed class SecretScannerTests : IDisposable
{
    private readonly string _work;

    public SecretScannerTests()
    {
        _work = Path.Combine(Path.GetTempPath(), "autocoder-secret", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_work);
    }

    [Fact]
    public void Allows_clean_source_change()
    {
        Write("src/app.js", "export function ping() { return 'ok'; }\n");
        var ctx = Context("src/app.js");
        SecretScanner.Scan(ctx);
    }

    [Fact]
    public void Rejects_github_pat()
    {
        Write("src/config.js", "const token = 'ghp_abcdefghijklmnopqrstuvwxyz012345';\n");
        var ctx = Context("src/config.js");
        var ex = Assert.Throws<InvalidOperationException>(() => SecretScanner.Scan(ctx));
        Assert.Contains("github-pat", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_openai_key()
    {
        Write("src/env.js", "const k = 'sk-abcdefghijklmnopqrstuvwxyz0123456789';\n");
        var ctx = Context("src/env.js");
        var ex = Assert.Throws<InvalidOperationException>(() => SecretScanner.Scan(ctx));
        Assert.Contains("openai-key", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_jwt()
    {
        Write("src/auth.js", "const jwt = 'eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIiwibmFtZSI6IkpvaG4ifQ.signaturepartxx';\n");
        var ctx = Context("src/auth.js");
        var ex = Assert.Throws<InvalidOperationException>(() => SecretScanner.Scan(ctx));
        Assert.Contains("jwt", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Skips_lockfile_even_if_marker_present()
    {
        Write("package-lock.json", "ghp_abcdefghijklmnopqrstuvwxyz012345\n");
        var ctx = Context("package-lock.json");
        SecretScanner.Scan(ctx);
    }

    [Fact]
    public void Ignores_guid_and_hex_hashes()
    {
        Write("src/ids.js", "const id = '550e8400-e29b-41d4-a716-446655440000'; const sha = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';\n");
        var ctx = Context("src/ids.js");
        SecretScanner.Scan(ctx);
    }

    [Fact]
    public void Does_not_scan_unchanged_tree()
    {
        Write("src/hidden.js", "const token = 'ghp_abcdefghijklmnopqrstuvwxyz012345';\n");
        var ctx = TestContext.New(_work);
        SecretScanner.Scan(ctx);
    }

    private void Write(string rel, string body)
    {
        var full = Path.Combine(_work, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, body);
    }

    private PipelineContext Context(string changed)
    {
        var ctx = TestContext.New(_work);
        ctx.ChangedRelativePaths.Add(changed);
        return ctx;
    }

    public void Dispose()
    {
        try { Directory.Delete(_work, recursive: true); } catch { /* ignore */ }
    }
}
