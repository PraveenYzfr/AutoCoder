namespace AutoCoder.Core.Config;

public static class DotEnvLoader
{
    public static void Load(string? path = null)
    {
        foreach (var candidate in CandidatePaths(path))
        {
            if (!File.Exists(candidate))
                continue;

            foreach (var raw in File.ReadAllLines(candidate))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                    continue;

                var idx = line.IndexOf('=');
                if (idx <= 0)
                    continue;

                var key = line[..idx].Trim();
                var value = line[(idx + 1)..].Trim().Trim('"').Trim('\'');
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                // Do not override vars already set in the process/shell.
                if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)))
                    Environment.SetEnvironmentVariable(key, value);
            }

            return;
        }
    }

    private static IEnumerable<string> CandidatePaths(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            yield return Path.GetFullPath(path);
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                var candidate = Path.Combine(dir.FullName, ".env");
                if (seen.Add(candidate))
                    yield return candidate;
                dir = dir.Parent;
            }
        }
    }
}
