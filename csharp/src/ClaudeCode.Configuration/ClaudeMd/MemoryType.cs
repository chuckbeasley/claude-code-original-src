namespace ClaudeCode.Configuration.ClaudeMd;

/// <summary>
/// Identifies the discovery tier a CLAUDE.md file was loaded from,
/// which determines its position in the final prompt (managed first, local last).
/// </summary>
public enum MemoryType
{
    Managed,    // /etc/claude-code/CLAUDE.md  (or ProgramData equivalent on Windows)
    User,       // ~/.claude/CLAUDE.md
    Project,    // ./CLAUDE.md, ./.claude/CLAUDE.md, ./.claude/rules/*.md
    Local,      // ./CLAUDE.local.md
}
