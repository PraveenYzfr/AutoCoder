using AutoCoder.Core.Runs;

namespace AutoCoder.Core.Llm;

/// <summary>
/// App-side hard stop. Google Cloud budgets alert, they do not cap.
/// Override with AUTOCODER_LLM_DAILY_CALL_BUDGET (0 = unlimited).
/// </summary>
public static class LlmDailyBudget
{
    public static (int Used, int Cap) Snapshot()
    {
        var raw = Environment.GetEnvironmentVariable("AUTOCODER_LLM_DAILY_CALL_BUDGET");
        var cap = 0;
        if (!string.IsNullOrWhiteSpace(raw) && int.TryParse(raw, out var parsed))
            cap = parsed;
        var used = 0;
        var path = Path.Combine(RunWorkspace.AppRoot(), "llm-budget", $"{DateTime.UtcNow:yyyy-MM-dd}.txt");
        if (File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(), out var existing))
            used = existing;
        return (used, cap);
    }

    public static void Consume(int calls = 1)
    {
        var raw = Environment.GetEnvironmentVariable("AUTOCODER_LLM_DAILY_CALL_BUDGET");
        // 0 = unlimited. A real ticket uses scout + plan + many coding turns; 20 was a Gemini leftover.
        var cap = 0;
        if (!string.IsNullOrWhiteSpace(raw) && int.TryParse(raw, out var parsed))
            cap = parsed;
        if (cap <= 0)
            return;

        var dir = Path.Combine(RunWorkspace.AppRoot(), "llm-budget");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{DateTime.UtcNow:yyyy-MM-dd}.txt");
        var used = 0;
        if (File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(), out var existing))
            used = existing;
        used += Math.Max(1, calls);
        File.WriteAllText(path, used.ToString());
        if (used > cap)
        {
            throw new InvalidOperationException(
                $"AutoCoder daily LLM budget exhausted ({used}/{cap}). "
                + "Set AUTOCODER_LLM_DAILY_CALL_BUDGET or wait until UTC midnight.");
        }
    }
}
