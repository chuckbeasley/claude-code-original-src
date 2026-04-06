namespace ClaudeCode.Services.Skills;

using ClaudeCode.Configuration;

/// <summary>
/// Represents a skill loaded from a Markdown file in a <c>.claude/skills/</c> directory.
/// </summary>
public record SkillDefinition
{
    /// <summary>
    /// The skill's name. Derived from the filename when no <c>name:</c> frontmatter key is present.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Optional one-line description from the frontmatter <c>description:</c> key.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// The body content of the skill file that is injected as the skill's prompt.
    /// When frontmatter is present this is the content after the closing <c>---</c> delimiter.
    /// </summary>
    public required string Prompt { get; init; }

    /// <summary>Absolute path to the skill file on disk.</summary>
    public required string FilePath { get; init; }
}

/// <summary>
/// Discovers and loads <see cref="SkillDefinition"/> instances from <c>.claude/skills/</c>
/// directories. Project skills take precedence over global skills when names collide.
/// </summary>
public static class SkillLoader
{
    /// <summary>
    /// Loads all skills visible from <paramref name="cwd"/>.
    /// Project skills (<c>{cwd}/.claude/skills/</c>) are discovered first and take precedence;
    /// global skills (<c>~/.claude/skills/</c>) are added for any name not already present.
    /// </summary>
    /// <param name="cwd">Current working directory. Must not be null or whitespace.</param>
    /// <returns>A list of <see cref="SkillDefinition"/> instances, in discovery order.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="cwd"/> is null or whitespace.</exception>
    public static List<SkillDefinition> LoadSkills(string cwd)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cwd);

        var skills = new List<SkillDefinition>();

        // Project skills — higher priority; loaded first.
        LoadFromDirectory(Path.Combine(cwd, ".claude", "skills"), skills);

        // Global skills — only added when their name is not already registered.
        var globalDir = Path.Combine(ConfigPaths.ClaudeHomeDir, "skills");
        LoadFromDirectory(globalDir, skills);

        // Bundled skills — lowest priority; skipped when a user-authored skill has the same name.
        AppendBundledSkills(BundledSkillRegistry.GetAlwaysOnSkills(), skills);
        AppendBundledSkills(BundledSkillRegistry.GetFlaggedSkills(), skills);

        return skills;
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private static void LoadFromDirectory(string dir, List<SkillDefinition> skills)
    {
        if (!Directory.Exists(dir)) return;

        foreach (var file in Directory.GetFiles(dir, "*.md")
            .Order(StringComparer.OrdinalIgnoreCase))
        {
            var skill = ParseSkillFile(file);
            if (skill is null) continue;

            // Project skills already loaded win over global skills with the same name.
            if (!skills.Any(s => s.Name.Equals(skill.Name, StringComparison.OrdinalIgnoreCase)))
                skills.Add(skill);
        }
    }

    private static SkillDefinition? ParseSkillFile(string path)
    {
        try
        {
            var content = File.ReadAllText(path);

            // Default name is the filename without extension; may be overridden by frontmatter.
            var name = Path.GetFileNameWithoutExtension(path);
            string? description = null;
            string prompt;

            if (content.StartsWith("---", StringComparison.Ordinal))
            {
                var endIdx = content.IndexOf("---", 3, StringComparison.Ordinal);
                if (endIdx > 0)
                {
                    var frontmatter = content[3..endIdx].Trim();
                    prompt = content[(endIdx + 3)..].Trim();

                    foreach (var line in frontmatter.Split('\n'))
                    {
                        var colonIdx = line.IndexOf(':');
                        if (colonIdx < 0) continue;

                        var key   = line[..colonIdx].Trim().ToLowerInvariant();
                        var value = line[(colonIdx + 1)..].Trim();

                        switch (key)
                        {
                            case "name":        name        = value; break;
                            case "description": description = value; break;
                        }
                    }
                }
                else
                {
                    // Opening --- present but no closing --- found; treat entire file as prompt.
                    prompt = content;
                }
            }
            else
            {
                prompt = content;
            }

            return new SkillDefinition
            {
                Name        = name,
                Description = description,
                Prompt      = prompt,
                FilePath    = path,
            };
        }
        catch
        {
            // IO errors or unexpected parse failures produce a silent skip.
            return null;
        }
    }

    private static void AppendBundledSkills(IReadOnlyList<SkillDefinition> bundled, List<SkillDefinition> skills)
    {
        foreach (var skill in bundled)
        {
            if (!skills.Any(s => s.Name.Equals(skill.Name, StringComparison.OrdinalIgnoreCase)))
                skills.Add(skill);
        }
    }
}
