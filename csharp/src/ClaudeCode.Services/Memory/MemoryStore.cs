namespace ClaudeCode.Services.Memory;

using System.Text;
using System.Text.RegularExpressions;
using ClaudeCode.Configuration;

/// <summary>
/// Represents a single parsed memory entry loaded from a topic file in the memory directory.
/// </summary>
public record MemoryEntry
{
    /// <summary>The human-readable name of the memory entry (from frontmatter).</summary>
    public required string Name { get; init; }

    /// <summary>One-line description used for relevance decisions (from frontmatter).</summary>
    public required string Description { get; init; }

    /// <summary>Memory type: "user", "feedback", "project", or "reference".</summary>
    public required string Type { get; init; }

    /// <summary>The body content of the memory file (everything after the closing frontmatter delimiter).</summary>
    public required string Content { get; init; }

    /// <summary>Absolute path to the file on disk.</summary>
    public required string FilePath { get; init; }
}

/// <summary>
/// File-based memory store that manages an index file (MEMORY.md) and per-topic memory files
/// under the per-project auto-memory directory (<c>~/.claude/projects/{project}/memory/</c>).
/// </summary>
public sealed partial class MemoryStore
{
    private readonly string _memoryDir;

    /// <summary>
    /// Initializes a new <see cref="MemoryStore"/> rooted at the auto-memory directory for
    /// the given working directory.
    /// </summary>
    /// <param name="cwd">The current working directory. Must not be null or whitespace.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="cwd"/> is null or whitespace.</exception>
    public MemoryStore(string cwd)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cwd);
        _memoryDir = ConfigPaths.AutoMemoryDir(cwd);
    }

    /// <summary>Absolute path to the memory directory for this project.</summary>
    public string MemoryDir => _memoryDir;

    /// <summary>
    /// Loads all memory entries from topic files in the memory directory.
    /// Files are read in alphabetical order; MEMORY.md is excluded.
    /// </summary>
    /// <returns>
    /// A list of successfully parsed <see cref="MemoryEntry"/> instances.
    /// Files that cannot be parsed are silently skipped.
    /// </returns>
    public List<MemoryEntry> LoadAll()
    {
        var entries = new List<MemoryEntry>();
        if (!Directory.Exists(_memoryDir)) return entries;

        foreach (var file in Directory.GetFiles(_memoryDir, "*.md")
            .Where(f => !f.EndsWith("MEMORY.md", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase))
        {
            var entry = ParseMemoryFile(file);
            if (entry is not null)
                entries.Add(entry);
        }

        return entries;
    }

    /// <summary>
    /// Loads the raw content of the MEMORY.md index file.
    /// </summary>
    /// <returns>
    /// The file content, or <see langword="null"/> when the index file does not exist.
    /// </returns>
    public string? LoadIndex()
    {
        var indexPath = IndexPath;
        return File.Exists(indexPath) ? File.ReadAllText(indexPath) : null;
    }

    /// <summary>
    /// Saves a memory entry to disk: writes the topic file with YAML frontmatter and updates
    /// the MEMORY.md index. If a file for the same name already exists it is overwritten.
    /// </summary>
    /// <param name="name">Human-readable name for the memory. Must not be null or whitespace.</param>
    /// <param name="description">One-line description. Must not be null.</param>
    /// <param name="type">Memory type ("user", "feedback", "project", or "reference"). Must not be null.</param>
    /// <param name="content">Body content. Must not be null.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="name"/>, <paramref name="description"/>, <paramref name="type"/>, or <paramref name="content"/> is null or whitespace.</exception>
    public void Save(string name, string description, string type, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(content);

        Directory.CreateDirectory(_memoryDir);

        var fileName = SanitizeFileName(name) + ".md";
        var filePath = Path.Combine(_memoryDir, fileName);

        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"name: {name}");
        sb.AppendLine($"description: {description}");
        sb.AppendLine($"type: {type}");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.Append(content);

        File.WriteAllText(filePath, sb.ToString());
        UpdateIndex(name, fileName);
    }

    /// <summary>
    /// Deletes the topic file for the named memory and removes its entry from MEMORY.md.
    /// The comparison is case-insensitive.
    /// </summary>
    /// <param name="name">The name of the memory to delete.</param>
    /// <returns>
    /// <see langword="true"/> when the entry was found and deleted;
    /// <see langword="false"/> when no entry with that name exists.
    /// </returns>
    public bool Delete(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var entries = LoadAll();
        var entry = entries.FirstOrDefault(
            e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        if (entry is null) return false;

        File.Delete(entry.FilePath);
        RemoveFromIndex(name);
        return true;
    }

    /// <summary>
    /// Returns the MEMORY.md index content suitable for injection into a system prompt,
    /// or an empty string when no memories have been saved.
    /// </summary>
    public string BuildMemoryPrompt()
    {
        var index = LoadIndex();
        return string.IsNullOrWhiteSpace(index) ? string.Empty : index;
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private string IndexPath => Path.Combine(_memoryDir, "MEMORY.md");

    private void UpdateIndex(string name, string fileName)
    {
        var lines = File.Exists(IndexPath)
            ? File.ReadAllLines(IndexPath).ToList()
            : [];

        // Remove any existing entry for this name before appending the fresh one.
        lines.RemoveAll(l => l.Contains($"[{name}]", StringComparison.OrdinalIgnoreCase));
        lines.Add($"- [{name}]({fileName})");

        File.WriteAllLines(IndexPath, lines);
    }

    private void RemoveFromIndex(string name)
    {
        if (!File.Exists(IndexPath)) return;

        var lines = File.ReadAllLines(IndexPath).ToList();
        lines.RemoveAll(l => l.Contains($"[{name}]", StringComparison.OrdinalIgnoreCase));
        File.WriteAllLines(IndexPath, lines);
    }

    private static MemoryEntry? ParseMemoryFile(string path)
    {
        try
        {
            var content = File.ReadAllText(path);
            if (!content.StartsWith("---", StringComparison.Ordinal)) return null;

            var endIdx = content.IndexOf("---", 3, StringComparison.Ordinal);
            if (endIdx < 0) return null;

            var frontmatter = content[3..endIdx].Trim();
            var body = content[(endIdx + 3)..].Trim();

            string? name = null, description = null, type = null;

            foreach (var line in frontmatter.Split('\n'))
            {
                var colonIdx = line.IndexOf(':');
                if (colonIdx < 0) continue;

                var key = line[..colonIdx].Trim().ToLowerInvariant();
                var value = line[(colonIdx + 1)..].Trim();

                switch (key)
                {
                    case "name":        name        = value; break;
                    case "description": description = value; break;
                    case "type":        type        = value; break;
                }
            }

            if (name is null || description is null || type is null) return null;

            return new MemoryEntry
            {
                Name        = name,
                Description = description,
                Type        = type,
                Content     = body,
                FilePath    = path,
            };
        }
        catch
        {
            // Any IO or parse failure produces a silent skip.
            return null;
        }
    }

    private static string SanitizeFileName(string name)
    {
        var sanitized = InvalidFileCharsRegex().Replace(name.ToLowerInvariant(), "_");
        return sanitized.Length > 50 ? sanitized[..50] : sanitized;
    }

    /// <summary>Matches any character that is not a word character or hyphen.</summary>
    [GeneratedRegex(@"[^\w\-]")]
    private static partial Regex InvalidFileCharsRegex();
}
