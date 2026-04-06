namespace ClaudeCode.Core.State;

using System.Runtime.InteropServices;

/// <summary>
/// Process-wide sandbox enforcement state. Stored in <c>ClaudeCode.Core</c> so that both
/// <c>ClaudeCode.Commands</c> and <c>ClaudeCode.Tools</c> can share it without creating
/// a circular project dependency.
/// </summary>
public static class SandboxModeState
{
    private static bool _enabled;

    /// <summary>Whether sandbox mode is currently active.</summary>
    public static bool IsEnabled => _enabled;

    /// <summary>Enables or disables sandbox mode.</summary>
    /// <param name="value"><see langword="true"/> to activate sandbox; <see langword="false"/> to deactivate.</param>
    public static void SetEnabled(bool value) => _enabled = value;

    /// <summary>
    /// Returns the command-line prefix array to prepend before the user's command when sandbox
    /// mode is active, or <see langword="null"/> when disabled or the platform has no supported
    /// wrapper.
    /// <list type="bullet">
    ///   <item>Linux — <c>unshare --user --pid --net --mount -r --</c></item>
    ///   <item>macOS — <c>sandbox-exec -p '(version 1)(deny default)(allow file-read*)' --</c></item>
    ///   <item>Windows — <see langword="null"/> (no external wrapper available)</item>
    /// </list>
    /// </summary>
    public static string[]? GetCommandPrefix()
    {
        if (!_enabled)
            return null;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return ["unshare", "--user", "--pid", "--net", "--mount", "-r", "--"];

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return ["sandbox-exec", "-p", "(version 1)(deny default)(allow file-read*)", "--"];

        // Windows: no external sandbox wrapper; enforcement reflected by IsEnabled flag only.
        return null;
    }
}
