namespace ClaudeCode.Configuration.ClaudeMd;

/// <summary>
/// Represents a single discovered and loaded CLAUDE.md (or rules) file,
/// including its content after HTML comment stripping and frontmatter removal.
/// </summary>
public record ClaudeMdFile
{
    /// <summary>The absolute path to this file on disk.</summary>
    public required string Path { get; init; }

    /// <summary>The discovery tier this file was loaded from.</summary>
    public required MemoryType Type { get; init; }

    /// <summary>
    /// The processed file content: HTML comments stripped, frontmatter removed,
    /// and @include directives replaced (included files are separate entries in the list).
    /// </summary>
    public required string Content { get; init; }

    /// <summary>
    /// The absolute path of the file that @included this one, or <see langword="null"/>
    /// if this file was discovered directly (not via an @include directive).
    /// </summary>
    public string? Parent { get; init; }
}
