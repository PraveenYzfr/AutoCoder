using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AutoCoder.Core.GitHub;

/// <summary>
/// Branch names for product-repo PRs: one unique branch per AutoCoder run on a ticket.
/// </summary>
public static partial class AutocoderBranchNaming
{
    [GeneratedRegex(@"^autocoder/[a-z]+-\d+-[a-f0-9]{8}$", RegexOptions.Compiled)]
    private static partial Regex BranchPattern();

    /// <summary>
    /// e.g. <c>autocoder/scrum-9-a1b2c3d4</c> — ticket key plus an 8-char hex suffix derived from the run id.
    /// </summary>
    public static string ForTicket(string ticketKey, string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ticketKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        var key = ticketKey.ToLowerInvariant();
        var suffix = ShortHash(runId);
        return $"autocoder/{key}-{suffix}";
    }

    internal static string ShortHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes)[..8].ToLowerInvariant();
    }

    internal static bool MatchesPattern(string branchName) => BranchPattern().IsMatch(branchName);
}
