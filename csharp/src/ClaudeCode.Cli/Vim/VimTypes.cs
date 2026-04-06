namespace ClaudeCode.Cli.Vim;

public enum VimMode { Normal, Insert, Visual }

public record VimState(
    VimMode Mode,
    int CursorPos,       // index in the input buffer
    int? VisualAnchor,   // Visual mode start index
    string PendingOp,    // accumulated key sequence e.g. "d", "2", "di"
    int RepeatCount      // numeric prefix e.g. 3 in "3w"
)
{
    public static VimState Initial => new(VimMode.Insert, 0, null, "", 1);
    public static VimState NormalAt(int pos) => new(VimMode.Normal, pos, null, "", 1);
}
