namespace ClaudeCode.Services.Updates;

/// <summary>
/// Checks for a newer release of ClaudeCode on GitHub and prints an update notice when one is found.
/// The check is throttled to once per 24 hours using a marker file in the user's profile.
/// </summary>
public static class UpdateChecker
{
    private const string MarkerFile = "~/.claude/.last-update-check";

    /// <summary>
    /// Checks GitHub for the latest release and prints an update notification when a newer
    /// version is available. Runs at most once every 24 hours; fails silently on any error.
    /// </summary>
    /// <param name="currentVersion">The running version string, e.g. "0.1.0".</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task CheckAndPrintAsync(string currentVersion, CancellationToken ct)
    {
        var marker = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", ".last-update-check");

        // Only check once per day
        if (File.Exists(marker) &&
            (DateTime.UtcNow - File.GetLastWriteTimeUtc(marker)).TotalHours < 24)
            return;

        try
        {
            using var http = new System.Net.Http.HttpClient();
            http.Timeout = TimeSpan.FromSeconds(5);
            http.DefaultRequestHeaders.Add("User-Agent", "claude-code-csharp/" + currentVersion);
            var resp = await http.GetStringAsync(
                "https://api.github.com/repos/anthropics/claude-code/releases/latest", ct);
            using var doc = System.Text.Json.JsonDocument.Parse(resp);
            var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            var latest = tag.TrimStart('v');

            if (latest != currentVersion && !string.IsNullOrEmpty(latest))
                Spectre.Console.AnsiConsole.MarkupLine(
                    $"[yellow]Update available:[/] {currentVersion} → {latest}. Run [blue]/update[/] to learn more.");

            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            File.WriteAllText(marker, DateTime.UtcNow.ToString("O"));
        }
        catch { /* fail silently */ }
    }
}
