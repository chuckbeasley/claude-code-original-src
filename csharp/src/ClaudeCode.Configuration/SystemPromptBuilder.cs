namespace ClaudeCode.Configuration;

using ClaudeCode.Configuration.ClaudeMd;
using ClaudeCode.Configuration.Settings;

/// <summary>
/// Assembles the full system prompt from static instructions, date context,
/// CLAUDE.md files, and git repository context.
/// </summary>
public sealed class SystemPromptBuilder
{
    private readonly ClaudeMdLoader _claudeMdLoader = new();

    /// <summary>
    /// Builds an ordered list of named system prompt sections for the given working directory.
    /// </summary>
    /// <param name="cwd">The current working directory. Must not be null or whitespace.</param>
    /// <param name="settings">Optional resolved settings; reserved for future use.</param>
    /// <param name="extraDirs">Optional additional directories whose CLAUDE.md files are included.</param>
    /// <returns>Ordered list of <see cref="SystemPromptSection"/> values ready for assembly.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="cwd"/> is null or whitespace.</exception>
    public List<SystemPromptSection> Build(string cwd, SettingsJson? settings = null, IReadOnlyList<string>? extraDirs = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cwd);

        var sections = new List<SystemPromptSection>();

        // 1. Default system instructions
        sections.Add(new SystemPromptSection("default", GetDefaultInstructions()));

        // Brief mode — appended when enabled via /brief command.
        if (ClaudeCode.Core.State.ReplModeFlags.BriefMode)
        {
            sections[0] = sections[0] with
            {
                Content = sections[0].Content + "\n\nBe concise. Prefer shorter responses. Lead with the answer.",
            };
        }

        // 2. Date context
        sections.Add(new SystemPromptSection("date", $"Current date: {DateTime.UtcNow:yyyy-MM-dd}"));

        // 3. CLAUDE.md files — loaded and combined into a single prompt block
        var claudeMdFiles = _claudeMdLoader.LoadAll(cwd);
        var claudeMdContent = _claudeMdLoader.BuildPrompt(claudeMdFiles);
        if (!string.IsNullOrWhiteSpace(claudeMdContent))
        {
            sections.Add(new SystemPromptSection("claude_md", claudeMdContent));
        }

        // 4. Persistent memory — MEMORY.md index for this project.
        // Loading is best-effort; any I/O failure is silently suppressed so that a
        // missing or unreadable memory file never prevents the session from starting.
        var memoryIndexPath = Path.Combine(ConfigPaths.AutoMemoryDir(cwd), "MEMORY.md");
        if (File.Exists(memoryIndexPath))
        {
            try
            {
                var memoryContent = File.ReadAllText(memoryIndexPath);
                if (!string.IsNullOrWhiteSpace(memoryContent))
                {
                    sections.Add(new SystemPromptSection("memory",
                        $"<memory>\n{memoryContent}\n</memory>"));
                }
            }
            catch
            {
                // Intentionally suppressed — memory injection is non-critical.
            }
        }

        // 5. Git context — silently omitted when cwd is not inside a git repo
        var gitContext = GetGitContext(cwd);
        if (gitContext is not null)
        {
            sections.Add(new SystemPromptSection("git", gitContext));
        }

        // 6. Extra project directories added via /add-dir.
        if (extraDirs is { Count: > 0 })
        {
            foreach (var extraDir in extraDirs)
            {
                try
                {
                    var extraMdFiles = _claudeMdLoader.LoadAll(extraDir);
                    var extraMdContent = _claudeMdLoader.BuildPrompt(extraMdFiles);
                    if (!string.IsNullOrWhiteSpace(extraMdContent))
                        sections.Add(new SystemPromptSection($"claude_md_{Path.GetFileName(extraDir)}", extraMdContent));
                }
                catch { /* non-critical */ }
            }
        }

        return sections;
    }

    /// <summary>
    /// Convenience overload that concatenates all sections into a single string,
    /// separated by double newlines.
    /// </summary>
    /// <param name="cwd">The current working directory. Must not be null or whitespace.</param>
    /// <param name="settings">Optional resolved settings; reserved for future use.</param>
    /// <param name="extraDirs">Optional additional directories whose CLAUDE.md files are included.</param>
    /// <returns>The full system prompt as a single string.</returns>
    public string BuildText(string cwd, SettingsJson? settings = null, IReadOnlyList<string>? extraDirs = null)
    {
        var sections = Build(cwd, settings, extraDirs);
        return string.Join("\n\n", sections.Select(s => s.Content));
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private static string GetDefaultInstructions() =>
        """
        You are Claude, an AI assistant by Anthropic. You help users with software engineering tasks.
        You are running inside ClaudeCode, a CLI tool for interacting with Claude from the terminal.

        Be concise and direct. Lead with the answer, not the reasoning.
        When writing code, prefer simple, correct solutions.
        """;

    /// <summary>
    /// Returns a brief git context string when <paramref name="cwd"/> is inside a git repository,
    /// or <see langword="null"/> if not.  All I/O exceptions are silently suppressed.
    /// </summary>
    private static string? GetGitContext(string cwd)
    {
        try
        {
            var gitRoot = FindGitRoot(cwd);
            if (gitRoot is null)
                return null;

            return $"Working directory: {cwd}\nGit repository detected.";
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Walks ancestor directories starting at <paramref name="dir"/> until it finds one
    /// containing a <c>.git</c> entry, then returns that directory path.
    /// Returns <see langword="null"/> when no git root is found.
    /// </summary>
    private static string? FindGitRoot(string dir)
    {
        var current = dir;
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current, ".git")))
                return current;
            current = Path.GetDirectoryName(current);
        }
        return null;
    }
}

/// <summary>
/// A named section of the assembled system prompt, carrying its content as a plain string.
/// </summary>
/// <param name="Name">Logical identifier for the section (e.g. "default", "claude_md", "git").</param>
/// <param name="Content">The text content of this section.</param>
public record SystemPromptSection(string Name, string Content);
