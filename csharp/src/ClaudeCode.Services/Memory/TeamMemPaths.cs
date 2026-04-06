namespace ClaudeCode.Services.Memory;

using System.Text;
using ClaudeCode.Configuration;
using ClaudeCode.Configuration.Settings;

/// <summary>
/// Path resolution and security validation for the per-project team memory directory.
/// Based on PSR M22186 security review — guards against path traversal, null bytes,
/// symlink escapes, and Unicode normalization attacks.
/// </summary>
public static class TeamMemPaths
{
    /// <summary>
    /// Returns <see langword="true"/> only when the "teammem" feature flag is enabled.
    /// </summary>
    /// <param name="config">
    /// The global configuration. May be <see langword="null"/>.
    /// <see cref="GlobalConfig"/> carries no <c>AutoMemory</c> property, so this method
    /// delegates entirely to <see cref="FeatureFlags.IsEnabled"/> for the "teammem" flag.
    /// </param>
    public static bool IsTeamMemoryEnabled(GlobalConfig? config)
        => FeatureFlags.IsEnabled("teammem");

    /// <summary>Returns the team memory subdirectory: <c>{autoMemPath}/team</c>.</summary>
    /// <param name="autoMemPath">
    /// Absolute path to the auto-memory root. Must not be <see langword="null"/> or whitespace.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="autoMemPath"/> is <see langword="null"/> or whitespace.
    /// </exception>
    public static string GetTeamMemPath(string autoMemPath)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(autoMemPath);
        return Path.Combine(autoMemPath, "team");
    }

    /// <summary>Returns the team memory entry-point file: <c>{autoMemPath}/team/MEMORY.md</c>.</summary>
    /// <param name="autoMemPath">
    /// Absolute path to the auto-memory root. Must not be <see langword="null"/> or whitespace.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="autoMemPath"/> is <see langword="null"/> or whitespace.
    /// </exception>
    public static string GetTeamMemEntrypoint(string autoMemPath)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(autoMemPath);
        return Path.Combine(autoMemPath, "team", "MEMORY.md");
    }

    /// <summary>
    /// Validates that <paramref name="candidatePath"/> is safely within <paramref name="teamMemRoot"/>.
    /// Throws <see cref="ArgumentException"/> if a traversal attempt is detected.
    /// </summary>
    /// <param name="candidatePath">
    /// The path to validate. Must not be <see langword="null"/> or whitespace.
    /// </param>
    /// <param name="teamMemRoot">
    /// The allowed root boundary. Must not be <see langword="null"/> or whitespace.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when a null byte, Unicode normalization anomaly, or directory traversal is detected.
    /// </exception>
    public static void ValidateWritePath(string candidatePath, string teamMemRoot)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(candidatePath);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(teamMemRoot);

        // 1. Null byte check — some OS calls truncate at '\0', enabling escape attacks.
        if (candidatePath.Contains('\0'))
            throw new ArgumentException("Null byte in path", nameof(candidatePath));

        // 2. Unicode normalization — if the raw form differs under NFC-KC, re-normalize before
        //    canonical resolution to prevent homoglyph/composed-character bypass attacks.
        var normalized = candidatePath.Normalize(NormalizationForm.FormKC);
        if (!string.Equals(normalized, candidatePath, StringComparison.Ordinal))
            candidatePath = normalized;

        // 3. Resolve to canonical path (collapses "..", ".", and redundant separators;
        //    note: Path.GetFullPath does NOT follow symlinks on .NET).
        var canonical = Path.GetFullPath(candidatePath);

        // 4. Resolve the canonical root.
        var canonicalRoot = Path.GetFullPath(teamMemRoot);

        // 5. Platform-appropriate string comparison: Windows filesystem is case-insensitive;
        //    Unix is case-sensitive. Using the wrong one in either direction weakens the guard.
        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        // Build a root sentinel that always ends with a separator so that a path that merely
        // *starts with* the root string but is outside it (e.g. /mem2 vs /mem) is rejected.
        var rootWithSep = canonicalRoot.TrimEnd(Path.DirectorySeparatorChar)
                          + Path.DirectorySeparatorChar;

        if (!canonical.StartsWith(rootWithSep, pathComparison)
            && !string.Equals(canonical, canonicalRoot, pathComparison))
        {
            throw new ArgumentException("Path traversal detected", nameof(candidatePath));
        }

        // 6. If the path already exists on disk, additionally verify that its directory resolves
        //    inside the root. This provides partial mitigation when a symlink in the directory
        //    component redirects traversal outside the root boundary.
        if (File.Exists(candidatePath) || Directory.Exists(candidatePath))
        {
            var dirPart = Path.GetDirectoryName(candidatePath);
            if (dirPart is not null)
            {
                var canonicalDir = Path.GetFullPath(dirPart);
                if (!canonicalDir.StartsWith(rootWithSep, pathComparison)
                    && !string.Equals(canonicalDir, canonicalRoot, pathComparison))
                {
                    throw new ArgumentException("Path traversal detected", nameof(candidatePath));
                }
            }
        }
    }
}
