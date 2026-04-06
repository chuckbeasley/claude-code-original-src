namespace ClaudeCode.Services.Memory;

/// <summary>
/// Builds system-prompt sections for when both private and team memory are active.
/// </summary>
public static class TeamMemPrompts
{
    /// <summary>
    /// Builds the combined memory system-prompt section describing both private and shared
    /// team memory scopes, type taxonomy, and save guidance.
    /// </summary>
    /// <param name="autoMemDir">Path to the private memory directory.</param>
    /// <param name="teamMemDir">Path to the shared team memory directory.</param>
    /// <param name="skipIndex">
    /// When <see langword="true"/>, omits the "update MEMORY.md index" instruction.
    /// Defaults to <see langword="false"/>.
    /// </param>
    /// <returns>
    /// A formatted multi-line prompt block, or <see cref="string.Empty"/> if an unexpected
    /// error occurs after guard validation.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="autoMemDir"/> or <paramref name="teamMemDir"/> is
    /// <see langword="null"/> or whitespace.
    /// </exception>
    public static string BuildCombinedMemoryPrompt(
        string autoMemDir, string teamMemDir, bool skipIndex = false)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(autoMemDir);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(teamMemDir);

        try
        {
            // Trim trailing separators so paths render cleanly in the prompt body.
            var privatePath = autoMemDir.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var teamPath = teamMemDir.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("You have access to two memory scopes:");
            sb.AppendLine();
            sb.AppendLine($"PRIVATE MEMORY ({privatePath}):");
            sb.AppendLine("  Your personal notes visible only to you. Use for user preferences, feedback, personal reminders.");
            sb.AppendLine();
            sb.AppendLine($"SHARED TEAM MEMORY ({teamPath}):");
            sb.AppendLine("  Notes shared with all team members. Use for project context, coding standards, team conventions.");
            sb.AppendLine("  IMPORTANT: Never save API keys, passwords, tokens, or credentials in team memory.");
            sb.AppendLine();
            sb.AppendLine("Memory types:");
            sb.AppendLine("  - user: Personal preferences and behavioral notes (private)");
            sb.AppendLine("  - feedback: User corrections and feedback (private)");
            sb.AppendLine("  - project: Project context and technical details (can be team)");
            sb.AppendLine("  - reference: Reusable knowledge and patterns (can be team)");
            sb.AppendLine();
            sb.AppendLine("When saving to team memory, prefer the project or reference types.");
            sb.Append("When in doubt about sensitivity, use private memory.");

            if (!skipIndex)
            {
                sb.AppendLine();
                sb.Append("After saving any memory file, update the MEMORY.md index with a one-line summary of the new entry.");
            }

            return sb.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }
}
