namespace LessonC12.Services;

public static class LocalConfiguration
{
    // Environment variables and user secrets take precedence over the optional .env.
    public static void LoadDotEnv(string root)
    {
        var path = Path.Combine(root, ".env");
        if (!File.Exists(path)) return;
        foreach (var line in File.ReadLines(path))
        {
            var text = line.Trim();
            if (text.Length == 0 || text.StartsWith('#')) continue;
            var separator = text.IndexOf('=');
            if (separator < 1) continue;
            var key = text[..separator].Trim();
            if (Environment.GetEnvironmentVariable(key) is null)
                Environment.SetEnvironmentVariable(key, text[(separator + 1)..].Trim().Trim('"', '\''));
        }
    }

    public static bool Valid(string? value) => !string.IsNullOrWhiteSpace(value) && !value.StartsWith("your_", StringComparison.OrdinalIgnoreCase);
}
