namespace ClaudeCode.Cli.Vim;

/// <summary>
/// Context passed to every operator. Decouples operators from global state so they are
/// independently unit-testable. Mirrors the TS OperatorContext pattern.
/// </summary>
public sealed class OperatorContext
{
    public required string Buffer    { get; set; }
    public required int    CursorPos { get; set; }

    // Named registers (char → content). Default register is '\0'.
    private readonly Dictionary<char, string> _registers = new();

    public void   SetRegister(char reg, string value) => _registers[reg]  = value;
    public string GetRegister(char reg) => _registers.TryGetValue(reg, out var v) ? v : "";

    // Convenience: default yank register
    public string YankRegister
    {
        get => GetRegister('\0');
        set => SetRegister('\0', value);
    }
}

/// <summary>
/// Pure buffer-manipulation operations that implement Vim operator semantics.
/// All methods mutate <paramref name="ctx"/> and return nothing; callers copy
/// <c>ctx.Buffer</c> / <c>ctx.CursorPos</c> back into <see cref="VimState"/>.
/// </summary>
public static class VimOperators
{
    // ── Operator + motion (d/c/y + w/b/e/0/$) ─────────────────────────────

    /// <summary>Executes an operator over a motion range.</summary>
    public static void ExecuteOperatorMotion(
        char op, string motion, OperatorContext ctx, int count = 1)
    {
        var buf = ctx.Buffer;
        var cur = ctx.CursorPos;
        var dst = VimMotions.Apply(motion, buf, cur, count);

        int from, to;
        if (dst >= cur) { from = cur;  to = dst; }
        else            { from = dst;  to = cur; }
        to = Math.Min(to, buf.Length);

        ctx.YankRegister = buf[from..to];

        if (op == 'y') return;                        // yank only
        ctx.Buffer    = buf.Remove(from, to - from);
        ctx.CursorPos = Math.Min(from, Math.Max(0, ctx.Buffer.Length - 1));
    }

    /// <summary>Executes an operator over a text object (e.g. iw, ci(, ya").</summary>
    public static void ExecuteOperatorTextObject(
        char op, string textObj, OperatorContext ctx)
    {
        var buf = ctx.Buffer;
        var (from, to) = VimTextObjects.Resolve(textObj, buf, ctx.CursorPos);
        to = Math.Min(to, buf.Length);

        ctx.YankRegister = buf[from..to];

        if (op == 'y') return;
        ctx.Buffer    = buf.Remove(from, to - from);
        ctx.CursorPos = Math.Min(from, Math.Max(0, ctx.Buffer.Length - 1));
    }

    // ── Line operators (dd / cc / yy) ─────────────────────────────────────

    /// <summary>Implements dd, cc, yy (whole-buffer ops since REPL has no multi-line).</summary>
    public static void ExecuteLineOp(char op, OperatorContext ctx)
    {
        ctx.YankRegister = ctx.Buffer;
        if (op == 'y') return;
        ctx.Buffer    = "";
        ctx.CursorPos = 0;
    }

    // ── Single-char operations ─────────────────────────────────────────────

    /// <summary>x — delete character under cursor.</summary>
    public static void ExecuteX(OperatorContext ctx)
    {
        if (ctx.CursorPos >= ctx.Buffer.Length) return;
        ctx.YankRegister = ctx.Buffer[ctx.CursorPos].ToString();
        ctx.Buffer       = ctx.Buffer.Remove(ctx.CursorPos, 1);
        ctx.CursorPos    = Math.Min(ctx.CursorPos, Math.Max(0, ctx.Buffer.Length - 1));
    }

    /// <summary>p (after) / P (before) — paste from default register.</summary>
    public static void ExecutePaste(bool before, OperatorContext ctx)
    {
        if (string.IsNullOrEmpty(ctx.YankRegister)) return;
        var insertAt = before
            ? ctx.CursorPos
            : Math.Min(ctx.CursorPos + 1, ctx.Buffer.Length);

        ctx.Buffer    = ctx.Buffer.Insert(insertAt, ctx.YankRegister);
        ctx.CursorPos = insertAt + (before ? 0 : ctx.YankRegister.Length - 1);
    }

    /// <summary>r{char} — replace character under cursor.</summary>
    public static void ExecuteReplace(char ch, OperatorContext ctx)
    {
        if (ctx.CursorPos >= ctx.Buffer.Length) return;
        ctx.Buffer = ctx.Buffer
            .Remove(ctx.CursorPos, 1)
            .Insert(ctx.CursorPos, ch.ToString());
        // cursor stays at same position
    }

    /// <summary>~ — toggle case of character under cursor.</summary>
    public static void ExecuteToggleCase(OperatorContext ctx)
    {
        if (ctx.CursorPos >= ctx.Buffer.Length) return;
        var ch  = ctx.Buffer[ctx.CursorPos];
        var tog = char.IsUpper(ch) ? char.ToLower(ch) : char.ToUpper(ch);
        ctx.Buffer = ctx.Buffer
            .Remove(ctx.CursorPos, 1)
            .Insert(ctx.CursorPos, tog.ToString());
        ctx.CursorPos = Math.Min(ctx.CursorPos + 1, Math.Max(0, ctx.Buffer.Length - 1));
    }

    /// <summary>J — join next "line" (replaces first \n with a space).</summary>
    public static void ExecuteJoin(OperatorContext ctx)
    {
        var idx = ctx.Buffer.IndexOf('\n', ctx.CursorPos);
        if (idx < 0) return;
        ctx.Buffer    = ctx.Buffer.Remove(idx, 1).Insert(idx, " ");
        ctx.CursorPos = idx;
    }

    /// <summary>&gt;&gt; / &lt;&lt; — indent / de-indent (adds/removes leading spaces).</summary>
    public static void ExecuteIndent(bool indent, OperatorContext ctx)
    {
        if (indent)
        {
            ctx.Buffer    = "    " + ctx.Buffer;
            ctx.CursorPos = Math.Min(ctx.CursorPos + 4, ctx.Buffer.Length);
        }
        else
        {
            var spaces = 0;
            while (spaces < ctx.Buffer.Length && ctx.Buffer[spaces] == ' ' && spaces < 4) spaces++;
            if (spaces == 0) return;
            ctx.Buffer    = ctx.Buffer[spaces..];
            ctx.CursorPos = Math.Max(0, ctx.CursorPos - spaces);
        }
    }

    /// <summary>o (below) / O (above) — open new line and enter insert mode.
    /// In the REPL context this appends a newline to the buffer.</summary>
    public static void ExecuteOpenLine(bool above, OperatorContext ctx)
    {
        if (above)
        {
            ctx.Buffer    = "\n" + ctx.Buffer;
            ctx.CursorPos = 0;
        }
        else
        {
            ctx.Buffer    = ctx.Buffer + "\n";
            ctx.CursorPos = ctx.Buffer.Length;
        }
    }
}
