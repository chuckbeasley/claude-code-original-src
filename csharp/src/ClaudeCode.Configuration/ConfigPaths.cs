namespace ClaudeCode.Configuration;

/// <summary>
/// Resolves all configuration file paths used by ClaudeCode.
/// All paths are computed on demand from environment state — no caching.
/// </summary>
public static class ConfigPaths
{
    /// <summary>
    /// The ~/.claude directory that owns all user-level ClaudeCode state.
    /// </summary>
    public static string ClaudeHomeDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    /// <summary>
    /// ~/.claude/claude.json — persisted global session state (GlobalConfig).
    /// </summary>
    public static string GlobalConfigPath => Path.Combine(ClaudeHomeDir, "claude.json");

    /// <summary>
    /// ~/.claude/settings.json — user-level settings (lowest priority).
    /// </summary>
    public static string UserSettingsPath => Path.Combine(ClaudeHomeDir, "settings.json");

    /// <summary>
    /// ~/.claude/CLAUDE.md — user-level memory/instructions document.
    /// </summary>
    public static string UserClaudeMdPath => Path.Combine(ClaudeHomeDir, "CLAUDE.md");

    /// <summary>
    /// {cwd}/.claude/settings.json — project-level settings checked into VCS.
    /// </summary>
    public static string ProjectSettingsPath(string cwd) =>
        Path.Combine(cwd, ".claude", "settings.json");

    /// <summary>
    /// {cwd}/.claude/settings.local.json — local machine overrides, not checked in.
    /// </summary>
    public static string LocalSettingsPath(string cwd) =>
        Path.Combine(cwd, ".claude", "settings.local.json");

    /// <summary>
    /// {cwd}/CLAUDE.md — project-root memory/instructions document.
    /// </summary>
    public static string ProjectClaudeMdPath(string cwd) =>
        Path.Combine(cwd, "CLAUDE.md");

    /// <summary>
    /// {cwd}/.claude/CLAUDE.md — alternative project-level instructions document.
    /// </summary>
    public static string ProjectClaudeDirMdPath(string cwd) =>
        Path.Combine(cwd, ".claude", "CLAUDE.md");

    /// <summary>
    /// {cwd}/CLAUDE.local.md — local (untracked) instructions document.
    /// </summary>
    public static string LocalClaudeMdPath(string cwd) =>
        Path.Combine(cwd, "CLAUDE.local.md");

    /// <summary>
    /// {cwd}/.claude/rules — directory for rule fragment files.
    /// </summary>
    public static string ProjectRulesDir(string cwd) =>
        Path.Combine(cwd, ".claude", "rules");

    /// <summary>
    /// System-managed policy settings path. On Windows this is under ProgramData;
    /// on Unix it is /etc/claude-code/managed-settings.json.
    /// </summary>
    public static string ManagedSettingsPath
    {
        get
        {
            if (OperatingSystem.IsWindows())
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "claude-code",
                    "managed-settings.json");

            return "/etc/claude-code/managed-settings.json";
        }
    }

    /// <summary>
    /// System-managed CLAUDE.md path. On Windows this is under ProgramData;
    /// on Unix it is /etc/claude-code/CLAUDE.md.
    /// </summary>
    public static string ManagedClaudeMdPath
    {
        get
        {
            if (OperatingSystem.IsWindows())
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "claude-code",
                    "CLAUDE.md");

            return "/etc/claude-code/CLAUDE.md";
        }
    }

    /// <summary>
    /// Per-project auto-memory directory, stored under ~/.claude/projects/{sanitized-cwd}/memory.
    /// </summary>
    public static string AutoMemoryDir(string cwd)
    {
        var sanitized = SanitizePath(cwd);
        return Path.Combine(ClaudeHomeDir, "projects", sanitized, "memory");
    }

    /// <summary>
    /// Replaces path separators and colons so that an absolute path can be used
    /// as a safe directory-name component inside ~/.claude/projects/.
    /// </summary>
    private static string SanitizePath(string path) =>
        path.Replace(Path.DirectorySeparatorChar, '-')
            .Replace(Path.AltDirectorySeparatorChar, '-')
            .Replace(':', '-')
            .TrimStart('-');
}
