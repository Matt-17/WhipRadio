namespace WhipRadio.AppHost;

/// <summary>
/// Loads a <c>.env</c> file from the repo root into the current process
/// environment, without overriding variables that are already set (so real
/// environment variables always win — important for CI/production). Cross-
/// platform: works identically on Windows, Linux, macOS, and WSL. The
/// <c>.env</c> file is gitignored; <c>.env.example</c> is the committed template.
/// </summary>
internal static class DotEnv
{
    public static void Load(string? directory = null)
    {
        var dir = directory ?? FindRepoRoot(AppContext.BaseDirectory);
        var path = Path.Combine(dir, ".env");
        if (!File.Exists(path))
        {
            return;
        }

        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();

            // Strip a single pair of surrounding quotes (single or double).
            if (value.Length >= 2 &&
                ((value[0] == '"' && value[^1] == '"') ||
                 (value[0] == '\'' && value[^1] == '\'')))
            {
                value = value[1..^1];
            }

            // Never clobber an existing env var — real environment wins.
            if (Environment.GetEnvironmentVariable(key) is null)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }

    private static string FindRepoRoot(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, ".gitignore")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return start;
    }
}
