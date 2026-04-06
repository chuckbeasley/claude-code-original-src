namespace ClaudeCode.Core.State;

/// <summary>
/// Process-wide flags that track active REPL mode toggles.
/// Stored here (in ClaudeCode.Core) so that both <c>ClaudeCode.Commands</c> and
/// <c>ClaudeCode.Configuration</c> can read and write them without a circular
/// project dependency.
/// </summary>
public static class ReplModeFlags
{
    /// <summary>
    /// When <see langword="true"/>, the /brief command is active and the system prompt
    /// should include a conciseness instruction.
    /// </summary>
    public static bool BriefMode { get; set; }

    /// <summary>
    /// When <see langword="true"/>, the REPL input loop applies vim-style normal/insert
    /// mode keybindings. Toggled by the <c>/vim</c> command.
    /// </summary>
    public static bool VimMode { get; set; }

    /// <summary>
    /// When <see langword="true"/>, voice mode is active: assistant responses are spoken
    /// via the platform TTS engine and voice input via Whisper is available when installed.
    /// Toggled by the <c>/voice</c> command.
    /// </summary>
    public static bool VoiceMode { get; set; }

    /// <summary>
    /// A one-shot system instruction injected as a prefix to the <em>next</em> user turn.
    /// Set by <c>/ultraplan</c>; consumed and cleared by <c>ReplSession</c> before the turn
    /// is submitted so the instruction applies exactly once.
    /// </summary>
    public static string? PendingSystemInjection { get; set; }

    /// <summary>
    /// When <see langword="true"/>, ultraplan mode is active and
    /// <see cref="UltraplanSystemPrompt"/> is appended to the system prompt on every turn.
    /// Toggled by the <c>/ultraplan</c> command.
    /// </summary>
    public static bool UltraplanActive { get; set; }

    /// <summary>
    /// The fixed system prompt text injected when <see cref="UltraplanActive"/> is
    /// <see langword="true"/>. Defined here (in ClaudeCode.Core) so that both
    /// ClaudeCode.Commands and ClaudeCode.Services can read it without a circular
    /// project dependency.
    /// </summary>
    public const string UltraplanSystemPrompt =
        "You are in ULTRAPLAN mode. Before responding to any request, first output a " +
        "structured plan with:\n" +
        "1. Goal analysis\n" +
        "2. Step-by-step approach\n" +
        "3. Potential risks\n" +
        "4. Success criteria\n" +
        "Then execute the plan.";

    /// <summary>
    /// When true, KAIROS assistant mode is active.
    /// The system prompt addendum is injected every request.
    /// Toggled by /assistant.
    /// </summary>
    public static bool KairosEnabled { get; set; }

    /// <summary>
    /// When true, Buddy mode is active.
    /// After each assistant turn a lightweight call generates a one-sentence context note.
    /// Toggled by /buddy.
    /// </summary>
    public static bool BuddyEnabled { get; set; }

    /// <summary>
    /// System prompt addendum injected when KairosEnabled is active.
    /// </summary>
    public const string KairosSystemPrompt =
        "--- ASSISTANT MODE ---\n" +
        "You are operating in assistant mode. Follow these rules every turn:\n" +
        "1. When the user's intent is ambiguous, ask exactly one clarifying question before acting.\n" +
        "2. When presenting choices, always use a numbered list (1. option  2. option ...).\n" +
        "3. Before executing any destructive operation (delete files, overwrite, reset, force-push),\n" +
        "   state what you are about to do and ask: \"Shall I proceed? (yes/no)\"\n" +
        "4. Begin every multi-step task with one sentence: \"I'll do X, then Y, then Z.\"\n" +
        "--- END ASSISTANT MODE ---";
}
