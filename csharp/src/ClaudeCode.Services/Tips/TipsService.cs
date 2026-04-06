namespace ClaudeCode.Services.Tips;

/// <summary>
/// Displays contextual usage tips to help users discover features.
/// </summary>
public static class TipsService
{
    private static readonly string[] _tips =
    [
        "Tip: Use [blue]/compact[/] when the context gets long to summarize and continue.",
        "Tip: Press [blue]Ctrl+R[/] to search command history.",
        "Tip: Use [blue]@filename[/] to inject file contents into your prompt.",
        "Tip: [blue]/add-dir[/] includes another directory's CLAUDE.md in the system prompt.",
        "Tip: [blue]/effort high[/] enables extended thinking for complex problems.",
        "Tip: [blue]/export[/] saves the conversation to a markdown file.",
        "Tip: Use [blue]/vim[/] to enable vim keybindings in the input.",
        "Tip: [blue]/memory[/] lets you add persistent notes to your CLAUDE.md files.",
        "Tip: [blue]/coordinator on[/] activates multi-agent orchestration mode.",
        "Tip: Use @filename to include file contents in your prompt.",
        "Tip: Press Tab to autocomplete file paths and slash commands.",
        "Tip: Use /bridge start to enable IDE extension integration.",
        "Tip: /thinkback shows analytics across all your sessions.",
    ];

    private static int _tipIndex = Random.Shared.Next(_tips.Length);
    private static DateTimeOffset _lastShown = DateTimeOffset.MinValue;

    // Counter file path for the 3-session rotation used by GetNextTip().
    private static readonly string _tipCounterPath =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", ".tip-counter");

    /// <summary>
    /// Displays one tip from the tip rotation using Spectre.Console grey markup.
    /// Subsequent calls within the same process are no-ops — each session sees at most one tip.
    /// </summary>
    public static void MaybeShowTip()
    {
        if (_lastShown != DateTimeOffset.MinValue) return;
        _lastShown = DateTimeOffset.UtcNow;
        Spectre.Console.AnsiConsole.MarkupLine("[grey]" + _tips[_tipIndex % _tips.Length] + "[/]");
        _tipIndex++;
    }

    /// <summary>
    /// Returns a tip to show, or <see langword="null"/> if no tip should be shown this session.
    /// A tip is shown once every 3 REPL sessions, tracked via a file counter at
    /// <c>~/.claude/.tip-counter</c>.
    /// The returned string is a Spectre.Console markup line (with [blue]...[/] tags)
    /// and should be wrapped in [grey]...[/] before printing.
    /// </summary>
    /// <returns>A tip string, or <see langword="null"/> when no tip is due.</returns>
    public static string? GetNextTip()
    {
        try
        {
            // Read and increment the persisted counter.
            int count = 0;
            if (File.Exists(_tipCounterPath))
            {
                var text = File.ReadAllText(_tipCounterPath).Trim();
                int.TryParse(text, out count);
            }

            count++;

            // Ensure directory exists before writing the counter.
            var dir = Path.GetDirectoryName(_tipCounterPath);
            if (dir is not null)
                Directory.CreateDirectory(dir);

            File.WriteAllText(_tipCounterPath, count.ToString());

            // Show a tip every 3rd session.
            if (count % 3 != 0)
                return null;

            var tip = _tips[_tipIndex % _tips.Length];
            _tipIndex = (_tipIndex + 1) % _tips.Length;
            return tip;
        }
        catch
        {
            // Counter I/O is best-effort; never block the REPL on failure.
            return null;
        }
    }
}
