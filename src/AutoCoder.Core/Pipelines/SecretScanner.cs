using System.Text.RegularExpressions;
using AutoCoder.Abstractions;
using AutoCoder.Core.Agent;
using AutoCoder.Core.Logging;
using Microsoft.Extensions.Logging;

namespace AutoCoder.Core.Pipelines;

/// <summary>Scan the agent diff (changed files), not the whole tree.</summary>
public static class SecretScanner
{
    private static readonly Regex Jwt = new(
        @"\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b",
        RegexOptions.Compiled);

    private static readonly Regex HighEntropy = new(
        @"\b[A-Za-z0-9+/=_\-]{32,}\b",
        RegexOptions.Compiled);

    private static readonly (string Name, Regex Pattern)[] Patterns =
    [
        ("pem-private-key", new Regex(@"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----", RegexOptions.Compiled)),
        ("github-pat", new Regex(@"\b(?:ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9_]{20,}\b", RegexOptions.Compiled)),
        ("github-fine-grained", new Regex(@"\bgithub_pat_[A-Za-z0-9_]{20,}\b", RegexOptions.Compiled)),
        ("slack-token", new Regex(@"\bxox[baprs]-[A-Za-z0-9-]{10,}\b", RegexOptions.Compiled)),
        ("aws-access-key", new Regex(@"\b(?:AKIA|ASIA)[0-9A-Z]{16}\b", RegexOptions.Compiled)),
        ("aws-secret-key", new Regex(@"aws_secret_access_key\s*[:=]\s*\S+", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("azure-storage-key", new Regex(@"AccountKey=[A-Za-z0-9+/=]{20,}", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("azure-sas", new Regex(@"sig=[A-Za-z0-9%+]{20,}", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("google-api-key", new Regex(@"\bAIza[0-9A-Za-z_\-]{20,}\b", RegexOptions.Compiled)),
        ("openai-key", new Regex(@"\bsk-(?:proj-)?[A-Za-z0-9_\-]{20,}\b", RegexOptions.Compiled)),
        ("anthropic-key", new Regex(@"\bsk-ant-[A-Za-z0-9_\-]{20,}\b", RegexOptions.Compiled)),
        ("stripe-key", new Regex(@"\b(?:sk|rk)_live_[A-Za-z0-9]{10,}\b", RegexOptions.Compiled)),
        ("npm-token", new Regex(@"\bnpm_[A-Za-z0-9]{20,}\b", RegexOptions.Compiled)),
        ("jwt", Jwt)
    ];

    public static void Scan(PipelineContext context)
    {
        var work = context.WorkDirectory ?? throw new InvalidOperationException("WorkDirectory required.");
        var files = FilesToScan(context, work);
        if (files.Count == 0)
        {
            Console.WriteLine("[SecretScan] No changed files to scan.");
            return;
        }

        foreach (var rel in files)
        {
            if (SkipPath(rel))
                continue;

            var full = Path.Combine(work, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full))
                continue;

            string text;
            try { text = File.ReadAllText(full); }
            catch { continue; }

            if (text.Length > 500_000)
                continue;

            foreach (var (name, pattern) in Patterns)
            {
                if (pattern.IsMatch(text))
                {
                    RunLog.Event(
                        "secret.hit",
                        context,
                        LogLevel.Error,
                        fields: [("file", rel), ("kind", name)]);
                    throw new InvalidOperationException($"Secret-like '{name}' found in {rel}. Refusing commit.");
                }
            }

            foreach (Match m in HighEntropy.Matches(text))
            {
                if (LooksLikeSecret(m.Value))
                {
                    RunLog.Event(
                        "secret.hit",
                        context,
                        LogLevel.Error,
                        fields: [("file", rel), ("kind", "high-entropy")]);
                    throw new InvalidOperationException($"High-entropy secret-like token found in {rel}. Refusing commit.");
                }
            }
        }

        Console.WriteLine($"[SecretScan] Scanned {files.Count} changed file(s); no secrets.");
        RunLog.Event("secret.clean", context, fields: ("files", files.Count));
    }

    private static List<string> FilesToScan(PipelineContext context, string work)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rel in context.ChangedRelativePaths)
        {
            if (!string.IsNullOrWhiteSpace(rel))
                set.Add(rel.Replace('\\', '/'));
        }

        foreach (var rel in GitChanged(work))
            set.Add(rel);

        return set.ToList();
    }

    private static IEnumerable<string> GitChanged(string work)
    {
        if (!Directory.Exists(Path.Combine(work, ".git")))
            return [];

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = work,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
            psi.ArgumentList.Add("diff");
            psi.ArgumentList.Add("--name-only");
            psi.ArgumentList.Add("HEAD");
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null)
                return [];
            var output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(10_000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return [];
            }

            return output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim().Replace('\\', '/'))
                .Where(line => line.Length > 0)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static bool SkipPath(string rel)
    {
        var p = rel.Replace('\\', '/');
        if (WorkspacePaths.IsIgnored(p))
            return true;
        if (p.StartsWith(".git/", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith(".autocoder/", StringComparison.OrdinalIgnoreCase))
            return true;
        var name = Path.GetFileName(p);
        return name.Equals("package-lock.json", StringComparison.OrdinalIgnoreCase)
               || name.Equals("yarn.lock", StringComparison.OrdinalIgnoreCase)
               || name.Equals("pnpm-lock.yaml", StringComparison.OrdinalIgnoreCase)
               || name.Equals("go.sum", StringComparison.OrdinalIgnoreCase)
               || p.EndsWith(".min.js", StringComparison.OrdinalIgnoreCase)
               || p.EndsWith(".map", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeSecret(string token)
    {
        if (token.Length < 32)
            return false;
        if (Guid.TryParse(token, out _))
            return false;
        if (Regex.IsMatch(token, "^[0-9a-fA-F]{32,}$"))
            return false;

        var distinct = token.Distinct().Count();
        if (distinct < 12)
            return false;

        var hasUpper = token.Any(char.IsUpper);
        var hasLower = token.Any(char.IsLower);
        var hasDigit = token.Any(char.IsDigit);
        return hasUpper && hasLower && hasDigit;
    }
}
