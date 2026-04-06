namespace ClaudeCode.Tools.Agent;

/// <summary>
/// Describes a named sub-agent loaded from a markdown file with YAML frontmatter.
/// </summary>
public record AgentDefinition
{
    /// <summary>Unique machine-readable name for this agent (derived from filename or frontmatter).</summary>
    public required string Name { get; init; }

    /// <summary>Optional human-readable description of what this agent does.</summary>
    public string? Description { get; init; }

    /// <summary>Optional model override for this agent (e.g. "claude-haiku-4-5-20251001").</summary>
    public string? Model { get; init; }

    /// <summary>The system prompt text for this agent (the markdown body after frontmatter).</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>
    /// Optional allow-list of tool names this agent may use.
    /// When <see langword="null"/> the agent inherits all non-Agent parent tools.
    /// </summary>
    public List<string>? AllowedTools { get; init; }
}

/// <summary>
/// Loads agent definitions from <c>.claude/agents/</c> directories.
/// Files are markdown with optional YAML frontmatter delimited by <c>---</c>.
/// Project-level agents (cwd) take precedence over global agents (~/.claude/agents/).
/// </summary>
public static class AgentDefinitionLoader
{
    /// <summary>
    /// Loads all agent definitions visible from <paramref name="cwd"/>.
    /// Project-level agents (in <c>{cwd}/.claude/agents/</c>) are loaded first and shadow
    /// global agents of the same name (case-insensitive).
    /// </summary>
    /// <param name="cwd">
    /// The current working directory to search for project-level agent definitions.
    /// Must not be <see langword="null"/> or whitespace.
    /// </param>
    /// <returns>
    /// An ordered list of <see cref="AgentDefinition"/> instances; project agents first,
    /// then any global agents not already shadowed by a project agent.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="cwd"/> is <see langword="null"/> or whitespace.
    /// </exception>
    public static List<AgentDefinition> LoadFromDirectory(string cwd)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cwd);

        var agents = new List<AgentDefinition>();

        // Project-level: {cwd}/.claude/agents/
        var projectAgentsDir = Path.Combine(cwd, ".claude", "agents");
        LoadDirectory(projectAgentsDir, agents);

        // Global: ~/.claude/agents/
        var globalAgentsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", "agents");
        LoadDirectory(globalAgentsDir, agents, excludeNames: agents.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase));

        return agents;
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Loads all <c>*.md</c> files from <paramref name="directory"/> into <paramref name="accumulator"/>,
    /// skipping any file whose parsed name is present in <paramref name="excludeNames"/>.
    /// </summary>
    private static void LoadDirectory(
        string directory,
        List<AgentDefinition> accumulator,
        HashSet<string>? excludeNames = null)
    {
        if (!Directory.Exists(directory))
            return;

        foreach (var file in Directory.GetFiles(directory, "*.md").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var agent = ParseAgentFile(file);
            if (agent is null)
                continue;

            if (excludeNames is not null && excludeNames.Contains(agent.Name))
                continue;

            accumulator.Add(agent);
        }
    }

    /// <summary>
    /// Parses a single agent markdown file. Returns <see langword="null"/> on any I/O or parse error.
    /// </summary>
    private static AgentDefinition? ParseAgentFile(string path)
    {
        try
        {
            var content = File.ReadAllText(path);

            // Name defaults to filename stem; may be overridden by frontmatter.
            var name = Path.GetFileNameWithoutExtension(path);

            string? description = null;
            string? model = null;
            List<string>? allowedTools = null;
            string? systemPrompt;

            // Parse YAML frontmatter when present (file starts with "---").
            if (content.StartsWith("---", StringComparison.Ordinal))
            {
                var endIdx = content.IndexOf("---", 3, StringComparison.Ordinal);
                if (endIdx > 0)
                {
                    var frontmatter = content[3..endIdx].Trim();
                    systemPrompt = content[(endIdx + 3)..].Trim();

                    foreach (var line in frontmatter.Split('\n'))
                    {
                        var trimmed = line.Trim();
                        var colonIdx = trimmed.IndexOf(':');
                        if (colonIdx < 0)
                            continue;

                        var key = trimmed[..colonIdx].Trim();
                        var value = trimmed[(colonIdx + 1)..].Trim();

                        switch (key.ToLowerInvariant())
                        {
                            case "name":
                                if (!string.IsNullOrWhiteSpace(value))
                                    name = value;
                                break;

                            case "description":
                                description = string.IsNullOrWhiteSpace(value) ? null : value;
                                break;

                            case "model":
                                model = string.IsNullOrWhiteSpace(value) ? null : value;
                                break;

                            case "allowed_tools" or "allowedtools":
                                var parsed = value.Split(',',
                                    StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                                allowedTools = parsed.Length > 0 ? [.. parsed] : null;
                                break;
                        }
                    }
                }
                else
                {
                    // "---" opener found but no closing "---"; treat whole file as system prompt.
                    systemPrompt = content;
                }
            }
            else
            {
                systemPrompt = string.IsNullOrWhiteSpace(content) ? null : content;
            }

            return new AgentDefinition
            {
                Name = name,
                Description = description,
                Model = model,
                SystemPrompt = systemPrompt,
                AllowedTools = allowedTools,
            };
        }
        catch
        {
            // Silently skip unreadable or malformed agent files.
            return null;
        }
    }
}
