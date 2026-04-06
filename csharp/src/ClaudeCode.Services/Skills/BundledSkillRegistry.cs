namespace ClaudeCode.Services.Skills;

using ClaudeCode.Configuration;

/// <summary>
/// Registers built-in bundled skills that are always available without
/// requiring a ~/.claude/skills/ file. Mirrors src/skills/bundled/index.ts.
/// </summary>
public static class BundledSkillRegistry
{
    /// <summary>
    /// Sentinel value used as <see cref="SkillDefinition.FilePath"/> for all bundled skills,
    /// which have no backing file on disk. Callers can check against this value to distinguish
    /// bundled skills from user-authored ones.
    /// </summary>
    public const string BundledPath = "<bundled>";

    /// <summary>Returns the always-on bundled skills.</summary>
    /// <returns>
    /// A read-only list of <see cref="SkillDefinition"/> instances that are unconditionally
    /// available regardless of feature-flag state.
    /// </returns>
    public static IReadOnlyList<SkillDefinition> GetAlwaysOnSkills() =>
    [
        new SkillDefinition
        {
            Name        = "update-config",
            Description = "Configure Claude Code settings via /config",
            Prompt      = "Use this skill to update Claude Code settings. Say /update-config to see options.",
            FilePath    = BundledPath,
        },
        new SkillDefinition
        {
            Name        = "keybindings",
            Description = "Show available keyboard shortcuts",
            Prompt      = "Use this skill to show keyboard shortcuts. Say /keybindings to list all shortcuts.",
            FilePath    = BundledPath,
        },
        new SkillDefinition
        {
            Name        = "verify",
            Description = "Verify implementation correctness",
            Prompt      = "Use this skill to verify that code is correct. Run tests, check output, confirm against spec.",
            FilePath    = BundledPath,
        },
        new SkillDefinition
        {
            Name        = "debug",
            Description = "Systematic debugging approach",
            Prompt      = "Use this skill for systematic debugging. Identify symptoms, form hypotheses, test one at a time.",
            FilePath    = BundledPath,
        },
        new SkillDefinition
        {
            Name        = "lorem-ipsum",
            Description = "Generate placeholder text",
            Prompt      = "Use this skill to generate lorem ipsum placeholder text. Specify word count or paragraph count.",
            FilePath    = BundledPath,
        },
        new SkillDefinition
        {
            Name        = "skillify",
            Description = "Convert task into a reusable skill",
            Prompt      = "Use this skill to convert a description into a reusable .md skill file in ~/.claude/skills/.",
            FilePath    = BundledPath,
        },
        new SkillDefinition
        {
            Name        = "remember",
            Description = "Save information to persistent memory",
            Prompt      = "Use this skill to save important information to ~/.claude/projects/memory/MEMORY.md for future sessions.",
            FilePath    = BundledPath,
        },
        new SkillDefinition
        {
            Name        = "simplify",
            Description = "Simplify and improve code quality",
            Prompt      = "Use this skill to review code for clarity, remove duplication, and improve maintainability.",
            FilePath    = BundledPath,
        },
        new SkillDefinition
        {
            Name        = "batch",
            Description = "Execute multiple tasks in parallel",
            Prompt      = "Use this skill to break a large request into independent subtasks and execute them in parallel.",
            FilePath    = BundledPath,
        },
        new SkillDefinition
        {
            Name        = "stuck",
            Description = "Get unstuck on a difficult problem",
            Prompt      = "Use this skill when stuck on a problem. Step back, restate the goal, try a different approach.",
            FilePath    = BundledPath,
        },
    ];

    /// <summary>Returns feature-flagged bundled skills that are currently enabled.</summary>
    /// <returns>
    /// A read-only list of <see cref="SkillDefinition"/> instances gated by feature flags.
    /// Returns an empty list when no relevant flags are enabled.
    /// </returns>
    public static IReadOnlyList<SkillDefinition> GetFlaggedSkills()
    {
        var skills = new List<SkillDefinition>(capacity: 3);

        if (FeatureFlags.IsEnabled("agent-triggers"))
        {
            skills.Add(new SkillDefinition
            {
                Name        = "loop",
                Description = "Run a task on a recurring interval",
                Prompt      = "Use this skill to schedule a recurring task.",
                FilePath    = BundledPath,
            });
        }

        if (FeatureFlags.IsEnabled("kairos-dream"))
        {
            skills.Add(new SkillDefinition
            {
                Name        = "dream",
                Description = "Background codebase analysis task",
                Prompt      = "Use this skill to start a background codebase analysis.",
                FilePath    = BundledPath,
            });
        }

        if (FeatureFlags.IsEnabled("experimental-skill-search"))
        {
            skills.Add(new SkillDefinition
            {
                Name        = "discover-skills",
                Description = "Search available skills",
                Prompt      = "Use this skill to discover available skills by keyword.",
                FilePath    = BundledPath,
            });
        }

        return skills;
    }
}
