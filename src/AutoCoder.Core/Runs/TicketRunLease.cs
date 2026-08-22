using AutoCoder.Core.Logging;

namespace AutoCoder.Core.Runs;

/// <summary>One live run per ticket at a time (file lease under artifacts/leases).</summary>
public static class TicketRunLease
{
    public static bool TryAcquire(string artifactsDirectory, string ticketKey, out string? skipReason)
    {
        skipReason = null;
        var key = Sanitize(ticketKey);
        var dir = Path.Combine(artifactsDirectory, "leases");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{key}.lease");

        if (File.Exists(path))
        {
            var written = File.GetLastWriteTimeUtc(path);
            var until = written.AddMinutes(45);
            if (DateTime.UtcNow < until)
            {
                skipReason = $"{ticketKey} skipped, lease held until {until:HH:mm} UTC";
                RunLog.Event(
                    "lease.skipped",
                    level: Microsoft.Extensions.Logging.LogLevel.Warning,
                    fields: [("ticket", ticketKey), ("untilUtc", until.ToString("O"))]);
                return false;
            }
        }

        File.WriteAllText(path, $"{DateTime.UtcNow:O}\n");
        RunLog.Event("lease.acquired", fields: ("ticket", ticketKey));
        return true;
    }

    public static void Touch(string artifactsDirectory, string ticketKey)
    {
        var path = Path.Combine(artifactsDirectory, "leases", $"{Sanitize(ticketKey)}.lease");
        if (File.Exists(path))
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
    }

    public static void Release(string artifactsDirectory, string ticketKey)
    {
        var path = Path.Combine(artifactsDirectory, "leases", $"{Sanitize(ticketKey)}.lease");
        try
        {
            if (File.Exists(path))
                File.Delete(path);
            RunLog.Event("lease.released", fields: ("ticket", ticketKey));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[lease] Release {ticketKey} failed: {ex.Message}");
        }
    }

    private static string Sanitize(string ticketKey) =>
        new string(ticketKey.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
}
