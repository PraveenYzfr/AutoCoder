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
            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(path);
            if (age < TimeSpan.FromMinutes(45))
            {
                skipReason = $"Ticket {ticketKey} already has a run from {age.TotalMinutes:F0}m ago.";
                RunLog.Event(
                    "lease.skipped",
                    level: Microsoft.Extensions.Logging.LogLevel.Warning,
                    fields: [("ticket", ticketKey), ("ageMinutes", age.TotalMinutes)]);
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

    private static string Sanitize(string ticketKey) =>
        new string(ticketKey.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
}
