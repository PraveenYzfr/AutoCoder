using AutoCoder.Core.Logging;

namespace AutoCoder.Core.Runs;

/// <summary>
/// Counts transient-failure attempts per ticket across separate runs (item 10 in
/// CLAUDE-AUTOCODER.md). A transient failure sends the ticket back to the poller instead of
/// "Agent Failure"; this file-based counter is what caps that at a fixed number of attempts so a
/// ticket cannot retry forever. Reset on success or once the cap is hit and the ticket is finally
/// failed.
/// </summary>
public static class TicketRetryTracker
{
    public static int IncrementAndGet(string artifactsDirectory, string ticketKey)
    {
        var path = PathFor(artifactsDirectory, ticketKey);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var count = ReadCount(path) + 1;
        File.WriteAllText(path, count.ToString());
        RunLog.Event("retry.transient", fields: [("ticket", ticketKey), ("attempt", count)]);
        return count;
    }

    public static int Peek(string artifactsDirectory, string ticketKey) =>
        ReadCount(PathFor(artifactsDirectory, ticketKey));

    public static void Reset(string artifactsDirectory, string ticketKey)
    {
        var path = PathFor(artifactsDirectory, ticketKey);
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[retry] Reset {ticketKey} failed: {ex.Message}");
        }
    }

    private static int ReadCount(string path) =>
        File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(), out var n) ? n : 0;

    private static string PathFor(string artifactsDirectory, string ticketKey) =>
        Path.Combine(artifactsDirectory, "retries", $"{Sanitize(ticketKey)}.count");

    private static string Sanitize(string ticketKey) =>
        new string(ticketKey.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
}
