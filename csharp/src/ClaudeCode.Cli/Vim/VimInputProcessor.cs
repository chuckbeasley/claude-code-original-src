namespace ClaudeCode.Cli.Vim;

public static class VimInputProcessor
{
    private static string _yankRegister = "";

    public static (string buffer, VimState state, bool submit) Process(
        ConsoleKeyInfo key, string buffer, VimState state)
    {
        return state.Mode switch
        {
            VimMode.Insert => ProcessInsert(key, buffer, state),
            VimMode.Normal => ProcessNormal(key, buffer, state),
            VimMode.Visual => ProcessVisual(key, buffer, state),
            _ => (buffer, state, false)
        };
    }

    private static (string, VimState, bool) ProcessInsert(ConsoleKeyInfo key, string buffer, VimState state)
    {
        // Escape → Normal mode
        if (key.Key == ConsoleKey.Escape)
            return (buffer, VimState.NormalAt(Math.Max(0, state.CursorPos - 1)), false);

        // Printable char
        if (!char.IsControl(key.KeyChar))
        {
            var nb = buffer.Insert(state.CursorPos, key.KeyChar.ToString());
            return (nb, state with { CursorPos = state.CursorPos + 1 }, false);
        }

        return key.Key switch
        {
            ConsoleKey.Backspace when state.CursorPos > 0 =>
                (buffer.Remove(state.CursorPos - 1, 1), state with { CursorPos = state.CursorPos - 1 }, false),
            ConsoleKey.Delete when state.CursorPos < buffer.Length =>
                (buffer.Remove(state.CursorPos, 1), state, false),
            ConsoleKey.LeftArrow =>
                (buffer, state with { CursorPos = Math.Max(0, state.CursorPos - 1) }, false),
            ConsoleKey.RightArrow =>
                (buffer, state with { CursorPos = Math.Min(buffer.Length, state.CursorPos + 1) }, false),
            ConsoleKey.Enter => (buffer, state, true),
            _ => (buffer, state, false)
        };
    }

    private static (string, VimState, bool) ProcessNormal(ConsoleKeyInfo key, string buffer, VimState state)
    {
        var ch = key.KeyChar;
        var pending = state.PendingOp + ch;
        var count = state.RepeatCount;

        // Numeric prefix
        if (char.IsDigit(ch) && (ch != '0' || state.PendingOp.Length > 0))
        {
            var num = state.PendingOp.Length > 0 && int.TryParse(state.PendingOp, out var n) ? n : 0;
            var digit = ch - '0';
            return (buffer, state with { PendingOp = "", RepeatCount = num * 10 + digit }, false);
        }

        // Mode transitions
        if (string.IsNullOrEmpty(state.PendingOp))
        {
            switch (ch)
            {
                case 'i': return (buffer, state with { Mode = VimMode.Insert, PendingOp = "", RepeatCount = 1 }, false);
                case 'I':
                    var ipos = VimMotions.Apply("^", buffer, state.CursorPos);
                    return (buffer, state with { Mode = VimMode.Insert, CursorPos = ipos, PendingOp = "", RepeatCount = 1 }, false);
                case 'a':
                    return (buffer, state with { Mode = VimMode.Insert, CursorPos = Math.Min(buffer.Length, state.CursorPos + 1), PendingOp = "", RepeatCount = 1 }, false);
                case 'A':
                    return (buffer, state with { Mode = VimMode.Insert, CursorPos = buffer.Length, PendingOp = "", RepeatCount = 1 }, false);
                case 'v': return (buffer, state with { Mode = VimMode.Visual, VisualAnchor = state.CursorPos, PendingOp = "", RepeatCount = 1 }, false);
                case 'o':
                    var nb = buffer + "\n";
                    return (nb, state with { Mode = VimMode.Insert, CursorPos = nb.Length, PendingOp = "", RepeatCount = 1 }, false);
            }
        }

        // Simple motions in normal mode
        var motions = new[] { "h", "l", "0", "$", "^", "w", "b", "e", "W", "B", "E" };
        if (Array.Exists(motions, m => m == ch.ToString()) && string.IsNullOrEmpty(state.PendingOp))
        {
            var np = VimMotions.Apply(ch.ToString(), buffer, state.CursorPos, count);
            return (buffer, state with { CursorPos = np, RepeatCount = 1 }, false);
        }

        // Operators: d, c, y, and their doubles (dd, cc, yy)
        if (ch == 'd' && state.PendingOp == "d")
        {
            // dd — delete whole line (here: clear buffer)
            _yankRegister = buffer;
            return ("", state with { CursorPos = 0, PendingOp = "", RepeatCount = 1 }, false);
        }
        if (ch == 'y' && state.PendingOp == "y")
        {
            _yankRegister = buffer;
            return (buffer, state with { PendingOp = "", RepeatCount = 1 }, false);
        }
        if (ch == 'c' && state.PendingOp == "c")
        {
            _yankRegister = buffer;
            return ("", state with { Mode = VimMode.Insert, CursorPos = 0, PendingOp = "", RepeatCount = 1 }, false);
        }

        // Operator + motion
        if (state.PendingOp is "d" or "y" or "c")
        {
            var op = state.PendingOp;
            int from, to;

            // Text object?
            if (ch is 'w' or '"' or '\'' or '(' or ')' or '[' or ']' or '{' or '}' or 'b' or 'B')
            {
                // need two-char text object like iw, aw
                return (buffer, state with { PendingOp = pending }, false);
            }
            else if (state.PendingOp.Length == 2 && state.PendingOp[1] is 'i' or 'a')
            {
                (from, to) = VimTextObjects.Resolve(state.PendingOp[1..] + ch, buffer, state.CursorPos);
            }
            else
            {
                // motion
                from = state.CursorPos;
                to = VimMotions.Apply(ch.ToString(), buffer, state.CursorPos, count);
                if (to < from) (from, to) = (to, from);
            }

            _yankRegister = buffer[from..to];
            var result = op == "y" ? buffer : buffer.Remove(from, to - from);
            var newPos = op == "y" ? state.CursorPos : Math.Min(from, result.Length);
            return (result, state with
            {
                Mode = op == "c" ? VimMode.Insert : VimMode.Normal,
                CursorPos = newPos, PendingOp = "", RepeatCount = 1
            }, false);
        }

        // Paste
        if (ch == 'p' && string.IsNullOrEmpty(state.PendingOp))
        {
            var nb = buffer.Insert(Math.Min(state.CursorPos + 1, buffer.Length), _yankRegister);
            return (nb, state with { CursorPos = state.CursorPos + 1, RepeatCount = 1 }, false);
        }
        if (ch == 'P' && string.IsNullOrEmpty(state.PendingOp))
        {
            var nb = buffer.Insert(state.CursorPos, _yankRegister);
            return (nb, state with { RepeatCount = 1 }, false);
        }

        // x — delete char under cursor
        if (ch == 'x' && string.IsNullOrEmpty(state.PendingOp) && state.CursorPos < buffer.Length)
        {
            _yankRegister = buffer[state.CursorPos].ToString();
            var nb = buffer.Remove(state.CursorPos, 1);
            return (nb, state with { CursorPos = Math.Min(state.CursorPos, nb.Length - 1), RepeatCount = 1 }, false);
        }

        // u — undo (not tracked yet — just return)
        if (ch == 'u') return (buffer, state with { PendingOp = "", RepeatCount = 1 }, false);

        // Enter submits
        if (key.Key == ConsoleKey.Enter && string.IsNullOrEmpty(state.PendingOp))
            return (buffer, state, true);

        // Accumulate operator
        if (ch is 'd' or 'y' or 'c' or 'i' or 'a')
            return (buffer, state with { PendingOp = pending }, false);

        // Escape clears pending
        if (key.Key == ConsoleKey.Escape)
            return (buffer, state with { PendingOp = "", RepeatCount = 1 }, false);

        return (buffer, state with { PendingOp = "", RepeatCount = 1 }, false);
    }

    private static (string, VimState, bool) ProcessVisual(ConsoleKeyInfo key, string buffer, VimState state)
    {
        var ch = key.KeyChar;
        var anchor = state.VisualAnchor ?? state.CursorPos;

        if (key.Key == ConsoleKey.Escape)
            return (buffer, VimState.NormalAt(state.CursorPos), false);

        // Motion in visual mode
        var motions = new[] { "h", "l", "0", "$", "^", "w", "b", "e" };
        if (Array.Exists(motions, m => m == ch.ToString()))
        {
            var np = VimMotions.Apply(ch.ToString(), buffer, state.CursorPos);
            return (buffer, state with { CursorPos = np }, false);
        }

        // d/x — delete selection
        if (ch is 'd' or 'x')
        {
            var (from, to) = anchor < state.CursorPos ? (anchor, state.CursorPos) : (state.CursorPos, anchor);
            to = Math.Min(to + 1, buffer.Length);
            _yankRegister = buffer[from..to];
            var nb = buffer.Remove(from, to - from);
            return (nb, VimState.NormalAt(Math.Min(from, nb.Length)), false);
        }

        // y — yank selection
        if (ch == 'y')
        {
            var (from, to) = anchor < state.CursorPos ? (anchor, state.CursorPos + 1) : (state.CursorPos, anchor + 1);
            to = Math.Min(to, buffer.Length);
            _yankRegister = buffer[from..to];
            return (buffer, VimState.NormalAt(state.CursorPos), false);
        }

        // c — change selection
        if (ch == 'c')
        {
            var (from, to) = anchor < state.CursorPos ? (anchor, state.CursorPos + 1) : (state.CursorPos, anchor + 1);
            to = Math.Min(to, buffer.Length);
            _yankRegister = buffer[from..to];
            var nb = buffer.Remove(from, to - from);
            return (nb, state with { Mode = VimMode.Insert, CursorPos = from, VisualAnchor = null, PendingOp = "" }, false);
        }

        return (buffer, state, false);
    }
}
