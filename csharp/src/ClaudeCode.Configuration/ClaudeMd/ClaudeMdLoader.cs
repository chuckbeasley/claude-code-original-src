namespace ClaudeCode.Configuration.ClaudeMd;

using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Discovers and loads all CLAUDE.md files applicable to a given working directory.
/// Files are returned in priority order: managed first, local last.
/// </summary>
public sealed class ClaudeMdLoader
{
    /// <summary>
    /// Discovers and loads all CLAUDE.md files for the given working directory.
    /// Returns them in priority order (managed first, local last).
    /// </summary>
    /// <param name="cwd">The current working directory. Must be a non-empty path.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="cwd"/> is null or whitespace.</exception>
    public List<ClaudeMdFile> LoadAll(string cwd)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cwd);

        var files = new List<ClaudeMdFile>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Managed CLAUDE.md (/etc/claude-code/CLAUDE.md or ProgramData equivalent)
        TryLoad(ConfigPaths.ManagedClaudeMdPath, MemoryType.Managed, files, visited);

        // 2. User CLAUDE.md (~/.claude/CLAUDE.md)
        TryLoad(ConfigPaths.UserClaudeMdPath, MemoryType.User, files, visited);

        // 3. Project CLAUDE.md files -- walk from git root down to cwd (outermost first)
        var projectDirs = GetProjectDirectories(cwd);
        foreach (var dir in projectDirs)
        {
            TryLoad(Path.Combine(dir, "CLAUDE.md"), MemoryType.Project, files, visited);
            TryLoad(Path.Combine(dir, ".claude", "CLAUDE.md"), MemoryType.Project, files, visited);
            LoadRulesDirectory(Path.Combine(dir, ".claude", "rules"), files, visited);
        }

        // 4. Local CLAUDE.md ({cwd}/CLAUDE.local.md)
        TryLoad(ConfigPaths.LocalClaudeMdPath(cwd), MemoryType.Local, files, visited);

        return files;
    }

    /// <summary>
    /// Builds the combined CLAUDE.md prompt content from all discovered files,
    /// labelling each section with its source path and memory type.
    /// </summary>
    /// <param name="files">The files returned by <see cref="LoadAll"/>.</param>
    /// <returns>A single string suitable for injection as a system prompt, or <see cref="string.Empty"/> if no files were found.</returns>
    public string BuildPrompt(List<ClaudeMdFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        if (files.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        foreach (var file in files)
        {
            if (string.IsNullOrWhiteSpace(file.Content)) continue;
            sb.AppendLine($"# From {GetDisplayPath(file.Path)} ({file.Type})");
            sb.AppendLine(file.Content.Trim());
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private void TryLoad(
        string path,
        MemoryType type,
        List<ClaudeMdFile> files,
        HashSet<string> visited,
        string? parent = null)
    {
        var fullPath = Path.GetFullPath(path);

        // visited.Add returns false if fullPath is already present -- prevents circular @includes
        // and deduplicates paths that resolve to the same file.
        if (!visited.Add(fullPath)) return;
        if (!File.Exists(fullPath)) return;

        try
        {
            var rawContent = File.ReadAllText(fullPath);
            var baseDir = Path.GetDirectoryName(fullPath) ?? string.Empty;

            var content = StripHtmlComments(rawContent);
            content = StripFrontmatter(content);
            content = ProcessIncludes(content, baseDir, type, files, visited, parentPath: fullPath);

            files.Add(new ClaudeMdFile
            {
                Path = fullPath,
                Type = type,
                Content = content,
                Parent = parent,
            });
        }
        catch (IOException)
        {
            // File became unreadable after existence check -- skip silently.
        }
        catch (UnauthorizedAccessException)
        {
            // Insufficient permissions -- skip silently.
        }
    }

    private void LoadRulesDirectory(string rulesDir, List<ClaudeMdFile> files, HashSet<string> visited)
    {
        if (!Directory.Exists(rulesDir)) return;

        // Sort alphabetically for deterministic load order across platforms
        var mdFiles = Directory.GetFiles(rulesDir, "*.md")
                               .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
        foreach (var mdFile in mdFiles)
        {
            TryLoad(mdFile, MemoryType.Project, files, visited);
        }
    }

    /// <summary>
    /// Scans <paramref name="content"/> for lines beginning with <c>@</c> (but not <c>@{</c>),
    /// treats them as include directives, loads the target file, and removes the directive line
    /// from the returned content.  Included files appear as separate entries in
    /// <paramref name="files"/> ordered before their parent.
    /// </summary>
    private string ProcessIncludes(
        string content,
        string baseDir,
        MemoryType type,
        List<ClaudeMdFile> files,
        HashSet<string> visited,
        string parentPath)
    {
        // Normalise line endings so Split behaves identically on Windows and Unix
        var normalized = content.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var result = new StringBuilder();

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith('@') && !trimmed.StartsWith("@{", StringComparison.Ordinal))
            {
                var includePath = trimmed[1..].Trim();
                if (includePath.Length > 0)
                {
                    var resolvedPath = ResolvePath(includePath, baseDir);
                    if (resolvedPath is not null && File.Exists(resolvedPath))
                    {
                        // Recursively load; visited set prevents infinite loops
                        TryLoad(resolvedPath, type, files, visited, parent: parentPath);
                        continue;  // Do not emit the @include line itself
                    }
                }
            }

            result.AppendLine(line);
        }

        return result.ToString();
    }

    /// <summary>
    /// Resolves an include path to an absolute path, honouring <c>~/</c>, rooted,
    /// and relative forms. Returns <see langword="null"/> if the path is invalid.
    /// </summary>
    private static string? ResolvePath(string includePath, string baseDir)
    {
        try
        {
            if (includePath.StartsWith("~/", StringComparison.Ordinal))
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return Path.GetFullPath(Path.Combine(home, includePath[2..]));
            }

            if (Path.IsPathRooted(includePath))
                return Path.GetFullPath(includePath);

            // Relative path (./foo, ../foo, or bare foo) -- resolve against baseDir
            return Path.GetFullPath(Path.Combine(baseDir, includePath));
        }
        catch (ArgumentException)
        {
            // includePath contained path-illegal characters
            return null;
        }
    }

    /// <summary>
    /// Walks from <paramref name="cwd"/> up to the git root (first ancestor containing a
    /// <c>.git</c> entry) or to the filesystem root when no git root exists.
    /// Returns directories in outermost-first order so that ancestor-level CLAUDE.md files
    /// are loaded before subdirectory ones.
    /// </summary>
    private static List<string> GetProjectDirectories(string cwd)
    {
        var dirs = new List<string>();
        var current = Path.GetFullPath(cwd);
        var root = Path.GetPathRoot(current);

        while (current is not null && current != root)
        {
            dirs.Add(current);

            if (Directory.Exists(Path.Combine(current, ".git")))
                break;  // Found git root -- stop walking up

            current = Path.GetDirectoryName(current);
        }

        // Reverse: git root (outermost) first, cwd (innermost) last
        dirs.Reverse();
        return dirs;
    }

    // Compiled once at class load; [\s\S]*? matches any character including newlines (non-greedy)
    private static readonly Regex _htmlCommentRegex =
        new(@"<!--[\s\S]*?-->", RegexOptions.Compiled);

    /// <summary>Removes all HTML comment blocks (including multiline) from <paramref name="content"/>.</summary>
    private static string StripHtmlComments(string content) =>
        _htmlCommentRegex.Replace(content, string.Empty);

    /// <summary>
    /// Removes YAML frontmatter (a <c>---</c>...<c>---</c> block at the very start of the file).
    /// Returns the content unchanged if no valid frontmatter block is found.
    /// </summary>
    private static string StripFrontmatter(string content)
    {
        if (!content.StartsWith("---", StringComparison.Ordinal)) return content;

        // Search for closing --- starting after the opening marker (index 3)
        var endIndex = content.IndexOf("---", 3, StringComparison.Ordinal);
        if (endIndex < 0) return content;

        // Skip past the closing --- delimiter and any immediately following newlines
        return content[(endIndex + 3)..].TrimStart('\r', '\n');
    }

    /// <summary>
    /// Returns a display-friendly path with the home directory replaced by <c>~</c>
    /// and all path separators normalised to forward slashes.
    /// </summary>
    private static string GetDisplayPath(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path.StartsWith(home, StringComparison.OrdinalIgnoreCase))
            return "~" + path[home.Length..].Replace('\\', '/');
        return path.Replace('\\', '/');
    }
}
