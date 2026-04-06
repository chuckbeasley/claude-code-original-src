namespace ClaudeCode.Cli.Vim;

/// <summary>
/// Inner state machine states used during a single normal-mode keypress sequence.
/// </summary>
public enum CommandState
{
    Idle,
    Count,
    Operator,
    OperatorCount,
    OperatorFind,
    OperatorTextObj,
    Find,
    G,
    OperatorG,
    Replace,
    Indent,
}

/// <summary>
/// Result returned by <see cref="VimTransitions.Transition"/>.
/// </summary>
public sealed class TransitionResult
{
    /// <summary>Next command state (cleared to <see cref="CommandState.Idle"/> after most ops).</summary>
    public required CommandState NextState { get; init; }

    /// <summary>New VimState after the transition. Null if unchanged.</summary>
    public VimState? NewVimState { get; init; }

    /// <summary>Whether to enter Insert mode after this operation (e.g. c, o, O, i, a).</summary>
    public bool EnterInsert { get; init; }

    /// <summary>Whether to submit the buffer (Enter in normal mode).</summary>
    public bool Submit { get; init; }
}

/// <summary>
/// Additional context needed by transitions that want to record dot-repeat or undo.
/// </summary>
public sealed class TransitionContext
{
    public required OperatorContext Buffer { get; init; }
    public Action? OnUndo { get; init; }
    public Action<string>? OnDotRepeat { get; init; }
}

/// <summary>
/// Normal-mode key dispatch for the Vim input processor. Routes a
/// <see cref="ConsoleKeyInfo"/> + current <see cref="CommandState"/> +
/// <see cref="VimState"/> to a <see cref="TransitionResult"/>.
///
/// Keeps all vim operator logic here and delegates to <see cref="VimOperators"/>
/// for buffer mutations, so <see cref="VimInputProcessor"/> only needs to copy
/// the resulting buffer/cursor back into its own state.
/// </summary>
public static class VimTransitions
{
    /// <summary>
    /// Computes the next state for a normal-mode keypress.
    /// </summary>
    public static TransitionResult Transition(
        CommandState state, ConsoleKeyInfo key, VimState vimState, TransitionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var ch    = key.KeyChar;
        var count = Math.Max(1, vimState.RepeatCount);
        var buf   = ctx.Buffer;

        // Escape always resets
        if (key.Key == ConsoleKey.Escape)
        {
            return Idle(vimState with { PendingOp = "", RepeatCount = 1 });
        }

        // Enter submits
        if (key.Key == ConsoleKey.Enter && state == CommandState.Idle)
            return new TransitionResult { NextState = CommandState.Idle, NewVimState = vimState, Submit = true };

        switch (state)
        {
            // ── IDLE ─────────────────────────────────────────────────────────────
            case CommandState.Idle:
            {
                // Numeric prefix (except 0 which is a motion)
                if (char.IsDigit(ch) && ch != '0')
                    return new TransitionResult
                    {
                        NextState   = CommandState.Count,
                        NewVimState = vimState with { RepeatCount = ch - '0', PendingOp = "" },
                    };

                switch (ch)
                {
                    // Mode entry
                    case 'i': return EnterInsert(vimState);
                    case 'I': return EnterInsert(vimState with { CursorPos = VimMotions.Apply("^", buf.Buffer, buf.CursorPos) });
                    case 'a': return EnterInsert(vimState with { CursorPos = Math.Min(buf.Buffer.Length, buf.CursorPos + 1) });
                    case 'A': return EnterInsert(vimState with { CursorPos = buf.Buffer.Length });
                    case 'o': VimOperators.ExecuteOpenLine(false, buf); return EnterInsert(vimState with { CursorPos = buf.CursorPos });
                    case 'O': VimOperators.ExecuteOpenLine(true,  buf); return EnterInsert(vimState with { CursorPos = buf.CursorPos });

                    // Motions
                    case 'h': case 'l': case '0': case '$': case '^':
                    case 'w': case 'b': case 'e': case 'W': case 'B': case 'E':
                    {
                        var np = VimMotions.Apply(ch.ToString(), buf.Buffer, buf.CursorPos, count);
                        return Idle(vimState with { CursorPos = np, RepeatCount = 1 });
                    }
                    case 'G': // G → end of buffer
                    {
                        return Idle(vimState with { CursorPos = Math.Max(0, buf.Buffer.Length - 1), RepeatCount = 1 });
                    }
                    case 'g': return new TransitionResult { NextState = CommandState.G, NewVimState = vimState };

                    // Operators
                    case 'd': return new TransitionResult { NextState = CommandState.Operator, NewVimState = vimState with { PendingOp = "d" } };
                    case 'c': return new TransitionResult { NextState = CommandState.Operator, NewVimState = vimState with { PendingOp = "c" } };
                    case 'y': return new TransitionResult { NextState = CommandState.Operator, NewVimState = vimState with { PendingOp = "y" } };

                    // Single-key ops
                    case 'x': VimOperators.ExecuteX(buf);              return Idle(SyncCursor(vimState, buf));
                    case 'p': VimOperators.ExecutePaste(false, buf);   return Idle(SyncCursor(vimState, buf));
                    case 'P': VimOperators.ExecutePaste(true,  buf);   return Idle(SyncCursor(vimState, buf));
                    case '~': VimOperators.ExecuteToggleCase(buf);     return Idle(SyncCursor(vimState, buf));
                    case 'J': VimOperators.ExecuteJoin(buf);           return Idle(SyncCursor(vimState, buf));
                    case 'r': return new TransitionResult { NextState = CommandState.Replace,  NewVimState = vimState };
                    case '>': return new TransitionResult { NextState = CommandState.Indent,   NewVimState = vimState with { PendingOp = ">" } };
                    case '<': return new TransitionResult { NextState = CommandState.Indent,   NewVimState = vimState with { PendingOp = "<" } };
                    case 'u': ctx.OnUndo?.Invoke();                    return Idle(vimState with { RepeatCount = 1 });
                }
                return Idle(vimState with { PendingOp = "", RepeatCount = 1 });
            }

            // ── COUNT ─────────────────────────────────────────────────────────────
            case CommandState.Count:
            {
                if (char.IsDigit(ch))
                    return new TransitionResult
                    {
                        NextState   = CommandState.Count,
                        NewVimState = vimState with { RepeatCount = vimState.RepeatCount * 10 + (ch - '0') },
                    };
                // Delegate to Idle with accumulated count
                return Transition(CommandState.Idle, key, vimState, ctx);
            }

            // ── OPERATOR ──────────────────────────────────────────────────────────
            case CommandState.Operator:
            {
                var op = vimState.PendingOp.Length > 0 ? vimState.PendingOp[0] : 'd';

                // Doubles: dd, cc, yy
                if (ch.ToString() == vimState.PendingOp)
                {
                    VimOperators.ExecuteLineOp(op, buf);
                    var afterMode = op == 'c' ? true : false;
                    return afterMode
                        ? EnterInsert(SyncCursor(vimState, buf))
                        : Idle(SyncCursor(vimState, buf));
                }

                // Text-object intro (i / a)
                if (ch is 'i' or 'a')
                    return new TransitionResult
                    {
                        NextState   = CommandState.OperatorTextObj,
                        NewVimState = vimState with { PendingOp = vimState.PendingOp + ch },
                    };

                // Motion
                var motions = new[] { "h","l","0","$","^","w","b","e","W","B","E" };
                if (Array.Exists(motions, m => m == ch.ToString()))
                {
                    VimOperators.ExecuteOperatorMotion(op, ch.ToString(), buf, count);
                    return op == 'c'
                        ? EnterInsert(SyncCursor(vimState, buf))
                        : Idle(SyncCursor(vimState, buf));
                }

                return Idle(vimState with { PendingOp = "", RepeatCount = 1 });
            }

            // ── OPERATOR TEXT OBJECT ──────────────────────────────────────────────
            case CommandState.OperatorTextObj:
            {
                var op      = vimState.PendingOp[0];
                var scope   = vimState.PendingOp[1]; // 'i' or 'a'
                var textObj = scope.ToString() + ch;
                VimOperators.ExecuteOperatorTextObject(op, textObj, buf);
                return op == 'c'
                    ? EnterInsert(SyncCursor(vimState, buf))
                    : Idle(SyncCursor(vimState, buf));
            }

            // ── G STATE ───────────────────────────────────────────────────────────
            case CommandState.G:
            {
                if (ch == 'g') // gg → go to start
                    return Idle(vimState with { CursorPos = 0, RepeatCount = 1 });
                return Idle(vimState with { PendingOp = "", RepeatCount = 1 });
            }

            // ── REPLACE ───────────────────────────────────────────────────────────
            case CommandState.Replace:
            {
                if (!char.IsControl(ch))
                    VimOperators.ExecuteReplace(ch, buf);
                return Idle(SyncCursor(vimState, buf));
            }

            // ── INDENT ────────────────────────────────────────────────────────────
            case CommandState.Indent:
            {
                if (ch.ToString() == vimState.PendingOp) // >> or <<
                {
                    VimOperators.ExecuteIndent(vimState.PendingOp == ">", buf);
                    return Idle(SyncCursor(vimState, buf));
                }
                return Idle(vimState with { PendingOp = "", RepeatCount = 1 });
            }

            default:
                return Idle(vimState with { PendingOp = "", RepeatCount = 1 });
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static TransitionResult Idle(VimState vs) =>
        new() { NextState = CommandState.Idle, NewVimState = vs };

    private static TransitionResult EnterInsert(VimState vs) =>
        new() { NextState = CommandState.Idle, NewVimState = vs with { Mode = VimMode.Insert, PendingOp = "", RepeatCount = 1 }, EnterInsert = true };

    private static VimState SyncCursor(VimState vs, OperatorContext ctx) =>
        vs with { CursorPos = ctx.CursorPos, PendingOp = "", RepeatCount = 1 };
}
